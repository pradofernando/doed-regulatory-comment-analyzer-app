import unittest

from analysis_requests import AnalysisRequestValidationError, create_analysis_request


class AnalysisRequestTests(unittest.TestCase):
    def create_request(self, payload):
        return create_analysis_request(
            payload,
            trigger_source="manual",
            default_document_id="ED-2025-SCC-0481-0001",
            default_batch_size=5,
            default_max_comments=None,
            default_models={
                "categorization": "gpt-5.4",
                "grouping": "gpt-5.4",
                "validation": "gpt-5.4",
            },
            allowed_models={"gpt-5.4"},
        )

    def test_manual_request_requires_explicit_comment_ids(self):
        with self.assertRaisesRegex(
            AnalysisRequestValidationError,
            "commentIds must contain at least one comment ID",
        ):
            self.create_request({})

    def test_manual_request_preserves_selected_comment_ids(self):
        request = self.create_request({
            "documentId": "ED-2025-SCC-0481-0001",
            "commentIds": ["comment-1", "comment-2"],
            "maxComments": 2,
        })

        self.assertEqual(["comment-1", "comment-2"], request["commentIds"])
        self.assertEqual(2, request["maxComments"])


if __name__ == "__main__":
    unittest.main()