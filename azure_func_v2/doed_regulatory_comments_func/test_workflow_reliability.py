import json
import unittest
from unittest.mock import AsyncMock, Mock, patch

from cosmos_runs import CosmosRunStore
from function_app import process_analysis_run, validate_grouped_analysis_with_agent


def grouped_analysis(submission_numbers):
    return {
        "categories": [{
            "comment_count": len(submission_numbers),
            "submission_numbers": submission_numbers,
            "csv_rows": submission_numbers,
        }],
        "total_comments": len(submission_numbers),
        "total_categories": 1,
    }


class _AgentContext:
    async def __aenter__(self):
        return object()

    async def __aexit__(self, exc_type, exc_value, traceback):
        return None


class WorkflowReliabilityTests(unittest.IsolatedAsyncioTestCase):
    @patch("function_app.run_foundry_agent", new_callable=AsyncMock)
    @patch("function_app.create_foundry_agent")
    async def test_invalid_validator_correction_preserves_valid_grouped_analysis(
        self,
        create_agent,
        run_agent,
    ):
        original = grouped_analysis([1, 2])
        invalid_correction = grouped_analysis([1, 1])
        create_agent.return_value = _AgentContext()
        run_agent.return_value = json.dumps({
            "status": "corrected",
            "collective_analysis": invalid_correction,
        })

        result = await validate_grouped_analysis_with_agent(
            [{"submission_number": 1}, {"submission_number": 2}],
            original,
            "validator",
        )

        self.assertIs(original, result)

    @patch("function_app.execute_analysis_request", new_callable=AsyncMock)
    @patch("function_app._get_cosmos_run_store")
    async def test_recorded_analysis_failure_is_not_rethrown(self, get_run_store, execute_request):
        request = {
            "runId": "run-1",
            "schemaVersion": 1,
            "triggerSource": "manual",
            "requestedAt": "2026-09-16T18:00:00Z",
            "documentId": "ED-2025-SCC-0481-0001",
            "commentIds": ["comment-1", "comment-2", "comment-3", "comment-4", "comment-5"],
            "maxComments": 5,
            "batchSize": 5,
            "models": {"categorization": None, "grouping": None, "validation": None},
            "runValidation": True,
        }
        message = Mock()
        message.get_body.return_value = json.dumps(request).encode("utf-8")
        run_store = Mock()
        run_store.try_start.return_value = True
        get_run_store.return_value = run_store
        execute_request.side_effect = ValueError("invalid grouped analysis")

        await process_analysis_run(message)

        saved_document = run_store.save_analysis.call_args.args[0]
        self.assertEqual("failed", saved_document["status"])
        self.assertEqual(5, saved_document["totalComments"])
        self.assertEqual("invalid grouped analysis", saved_document["errorMessage"])


class CosmosRunStoreTests(unittest.TestCase):
    def test_only_queued_runs_can_transition_to_running(self):
        store = CosmosRunStore.__new__(CosmosRunStore)
        store._runs = Mock()

        self.assertTrue(store.try_start("run-1", "2026-09-16T18:00:00Z"))

        self.assertEqual(
            "FROM c WHERE c.status = 'queued'",
            store._runs.patch_item.call_args.kwargs["filter_predicate"],
        )


if __name__ == "__main__":
    unittest.main()