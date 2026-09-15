import unittest
from unittest.mock import patch

from function_app import consolidate_comments_to_csv, get_detail_comment_text


class CommentConsolidationTests(unittest.TestCase):
    def test_reads_comment_text_from_detail_response(self):
        details = {"data": {"attributes": {"comment": "  Substantive detail text.  "}}}

        self.assertEqual("Substantive detail text.", get_detail_comment_text(details))

    @patch("function_app.time.sleep")
    @patch("function_app.get_comment_with_attachments")
    def test_uses_detail_text_when_list_response_omits_comment(self, get_details, _sleep):
        get_details.return_value = {
            "data": {
                "attributes": {
                    "comment": "Substantive detail text that must be sent to the analysis agent."
                }
            },
            "included": [],
        }
        comments = [{
            "number": 1,
            "comment_id": "comment-1",
            "comment": "",
            "has_attachments": False,
        }]

        rows = consolidate_comments_to_csv(comments, "test-key")

        self.assertEqual(
            "Substantive detail text that must be sent to the analysis agent.",
            rows[0]["comment_text"],
        )

    @patch("function_app.time.sleep")
    @patch("function_app.get_comment_with_attachments")
    def test_keeps_substantive_detail_text_that_mentions_attachments(self, get_details, _sleep):
        get_details.return_value = {
            "data": {"attributes": {"comment": "This substantive comment discusses the attached methodology."}},
            "included": [],
        }
        comments = [{"number": 1, "comment_id": "comment-1", "comment": "", "has_attachments": False}]

        rows = consolidate_comments_to_csv(comments, "test-key")

        self.assertEqual(
            "This substantive comment discusses the attached methodology.",
            rows[0]["comment_text"],
        )


if __name__ == "__main__":
    unittest.main()