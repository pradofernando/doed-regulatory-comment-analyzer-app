import json
import unittest
from unittest.mock import Mock, patch

import azure.functions as func

import function_app


class SubmitAnalysisRunTests(unittest.TestCase):
    @staticmethod
    def request(payload):
        return func.HttpRequest(
            method="POST",
            url="https://functions.example/api/analysis-runs",
            body=json.dumps(payload).encode("utf-8"),
            headers={"content-type": "application/json"},
        )

    def test_empty_payload_is_rejected_without_queueing(self):
        request_message = Mock()

        response = function_app.submit_analysis_run(self.request({}), request_message)

        self.assertEqual(400, response.status_code)
        request_message.set.assert_not_called()

    def test_comment_ids_are_queued(self):
        request_message = Mock()
        run_store = Mock()
        normalized_request = {
            "runId": "run-1",
            "documentId": "DOC-1",
            "commentIds": ["COMMENT-1", "COMMENT-2"],
            "maxComments": 2,
            "batchSize": 5,
            "models": {},
            "runValidation": False,
        }

        with (
            patch.object(function_app, "_create_request", return_value=normalized_request) as create_request,
            patch.object(function_app, "_get_cosmos_run_store", return_value=run_store),
            patch.object(function_app, "build_job_document", return_value={"id": "run-1"}),
        ):
            response = function_app.submit_analysis_run(
                self.request({"documentId": "DOC-1", "commentIds": ["COMMENT-1", "COMMENT-2"]}),
                request_message,
            )

        self.assertEqual(202, response.status_code)
        submitted_payload = create_request.call_args.args[0]
        self.assertEqual(["COMMENT-1", "COMMENT-2"], submitted_payload["commentIds"])
        request_message.set.assert_called_once_with(json.dumps(normalized_request))

    def test_request_body_is_read_only_once(self):
        request_message = Mock()
        request = ConsumingRequest({"commentIds": ["COMMENT-1", "COMMENT-2"]})

        with (
            patch.object(function_app, "_create_request", return_value={
                "runId": "run-1",
                "documentId": "DOC-1",
                "commentIds": ["COMMENT-1", "COMMENT-2"],
                "maxComments": 2,
                "batchSize": 5,
                "models": {},
                "runValidation": False,
            }),
            patch.object(function_app, "_get_cosmos_run_store", return_value=Mock()),
            patch.object(function_app, "build_job_document", return_value={"id": "run-1"}),
        ):
            response = function_app.submit_analysis_run(request, request_message)

        self.assertEqual(202, response.status_code)
        self.assertEqual(1, request.body_read_count)


class ConsumingRequest:
    def __init__(self, payload):
        self._body = json.dumps(payload).encode("utf-8")
        self.body_read_count = 0

    def get_body(self):
        self.body_read_count += 1
        if self.body_read_count > 1:
            return b""
        return self._body


if __name__ == "__main__":
    unittest.main()