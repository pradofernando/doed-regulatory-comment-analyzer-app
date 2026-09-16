import gzip
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from source_evidence import new_source, add_passage, finish_source, validate_evidence, validate_group_evidence, plain_text
from analysis_requests import create_analysis_request, AnalysisRequestValidationError
from cosmos_runs import build_analysis_document, serialize_categorization_payload
from function_app import consolidate_comments_to_csv, extract_document_pages, download_file, fetch_comments_from_api


class SourceEvidenceTests(unittest.TestCase):
    def source(self):
        result = new_source(1, {"comment_id": "COMMENT-1"})
        add_passage(result, "Keep public oversight.", "pdf", "Source.pdf", page=3)
        return finish_source(result)

    def test_preserves_pages_and_bounds_capture(self):
        source = self.source()
        add_passage(source, "x" * 30000, "docx", "Long.docx")
        finish_source(source)
        self.assertEqual(24000, sum(len(p["text"]) for p in source["passages"]))
        self.assertEqual(3, source["passages"][0]["pageNumber"])
        self.assertIsNone(source["passages"][1]["pageNumber"])
        self.assertEqual("partial", source["extractionStatus"])

    def test_quotes_must_match_own_source(self):
        source = self.source()
        result = {"evidence": [
            {"source_id": "s1-p1", "quote": "Keep public oversight"},
            {"source_id": "s2-p1", "quote": "Keep public oversight"},
            {"source_id": "s1-p1", "quote": "Invented words"},
        ]}
        validate_evidence(source, result)
        self.assertEqual(1, len(result["evidence"]))
        self.assertTrue(source["warnings"])

    def test_group_evidence_requires_membership_and_finding(self):
        source = self.source()
        group = {"submission_numbers": [1], "common_arguments": ["Oversight"], "evidence": [
            {"finding": "Oversight", "source_id": "s1-p1", "quote": "Keep public oversight"},
            {"finding": "Invented", "source_id": "s1-p1", "quote": "Keep public oversight"},
        ]}
        validate_group_evidence([source], {"categories": [group]})
        self.assertEqual(1, len(group["evidence"]))

    def test_hash_and_unreadable_status(self):
        source = self.source()
        self.assertEqual(source["contentHash"], self.source()["contentHash"])
        empty = finish_source(new_source(1, {"comment_id": "EMPTY"}))
        self.assertEqual("unreadable", empty["extractionStatus"])
        self.assertEqual("Safe text.", plain_text("<script>ignored</script><p>Safe text.</p>"))

    @patch("function_app.get_document_intelligence_client")
    def test_ocr_uses_actual_page_number(self, client):
        client.return_value.begin_analyze_document.return_value.result.return_value = SimpleNamespace(
            content="Page three.", pages=[SimpleNamespace(page_number=3, lines=[SimpleNamespace(content="Page three.")], spans=[])])
        self.assertEqual([(3, "Page three.")], extract_document_pages(b"%PDF-synthetic", "pdf"))
        self.assertEqual("1-100", client.return_value.begin_analyze_document.call_args.kwargs["pages"])

    @patch("function_app.requests.get")
    def test_unapproved_attachment_hosts_never_receive_credentials(self, get):
        with self.assertRaises(ValueError):
            download_file("https://untrusted.example.test/document.pdf", "synthetic-key")
        get.assert_not_called()

    @patch("function_app.requests.get")
    def test_http_failure_does_not_return_a_partial_success(self, get):
        import requests
        get.side_effect = requests.RequestException("synthetic fetch failure")
        with self.assertRaises(requests.RequestException):
            fetch_comments_from_api("ED-2026-SCC-1234", "synthetic-key")

    @patch("function_app.requests.get")
    def test_docket_id_is_not_shortened_as_if_it_were_a_document(self, get):
        get.return_value.json.return_value = {"data": [], "meta": {"numberOfPages": 1}}
        fetch_comments_from_api("ED-2026-SCC-1234", "synthetic-key", use_docket_filter=True)
        self.assertEqual("ED-2026-SCC-1234", get.call_args.kwargs["params"]["filter[docketId]"])

    @patch("function_app.extract_document_pages", return_value=[(2, "Actual attachment evidence.")])
    @patch("function_app.download_file", return_value=b"%PDF-synthetic")
    @patch("function_app.get_comment_with_attachments")
    def test_long_inline_text_does_not_skip_attachments(self, details, download, extraction):
        details.return_value = {
            "data": {"attributes": {"comment": "Inline text " * 30}},
            "included": [{"type": "attachments", "attributes": {"title": "Evidence.pdf", "fileFormats": [
                {"format": "pdf", "fileUrl": "https://downloads.regulations.gov/evidence.pdf"}]}}],
        }
        rows = consolidate_comments_to_csv([{"number": 1, "comment_id": "COMMENT-1", "comment": "Long inline " * 50}], "synthetic-key")
        source = rows[0]["source"]
        self.assertTrue(any(p["pageNumber"] == 2 and p["text"] == "Actual attachment evidence." for p in source["passages"]))
        self.assertEqual("complete", source["extractionStatus"])

    def request(self, metadata=None):
        payload = {"documentId": "ED-TEST"}
        if metadata is not None:
            payload["inputMetadata"] = metadata
        return create_analysis_request(payload, trigger_source="manual", default_document_id="ED-TEST",
                                       default_batch_size=5, default_max_comments=None,
                                       default_models={"categorization": None, "grouping": None, "validation": None})

    def test_input_metadata_is_backward_compatible_and_validated(self):
        self.assertIsNone(self.request()["inputMetadata"])
        result = self.request({"fetchedComments": 5, "availableComments": 20, "isSelection": True, "useDocketFilter": False})
        self.assertFalse(result["inputMetadata"]["useDocketFilter"])
        for invalid in ({"fetchedComments": True}, {"fetchedComments": 3, "useDocketFilter": "true"}, {"unexpected": 1}):
            with self.subTest(invalid=invalid), self.assertRaises(AnalysisRequestValidationError):
                self.request(invalid)

    def test_payload_round_trip_preserves_sanitized_evidence_and_original_response(self):
        request = self.request()
        source = self.source()
        result = {"totalComments": 1, "sources": [source], "provenance": {"scope": "selected"},
                  "groupedAnalysis": {"categories": []}, "categorizations": [{
                      "submission_number": 1, "comment_id": "COMMENT-1", "raw_response": '{"evidence":[{"source_id":"fake"}]}',
                      "categorization": {"primary_theme": "Oversight", "evidence": []},
                  }]}
        document = build_analysis_document(request, result, started_at="2026-01-01T00:00:00Z", completed_at="2026-01-01T00:01:00Z")
        item = document["categorizations"][0]
        self.assertEqual([], json.loads(item["parsedJson"])["evidence"])
        self.assertIn("fake", item["rawResponse"])
        payload = json.loads(gzip.decompress(serialize_categorization_payload(document)))
        self.assertEqual(2, payload["schemaVersion"])
        self.assertEqual(3, payload["sources"][0]["passages"][0]["pageNumber"])
        self.assertEqual("selected", payload["provenance"]["scope"])


if __name__ == "__main__":
    unittest.main()
