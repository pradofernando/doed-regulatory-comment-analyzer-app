import unittest

from structured_responses import iter_json_values, select_json_object


class StructuredResponseTests(unittest.TestCase):
    @staticmethod
    def select_result(response: str):
        return select_json_object(response, lambda value: "result" in value)

    def test_plain_object(self):
        self.assertEqual({"result": "ok"}, self.select_result('{"result":"ok"}'))

    def test_fenced_object(self):
        self.assertEqual({"result": "ok"}, self.select_result('```json\n{"result":"ok"}\n```'))

    def test_skips_tool_object(self):
        response = '{"query":"anything"}\n{"result":"ok"}'
        self.assertEqual({"result": "ok"}, self.select_result(response))

    def test_handles_prose_and_malformed_prefix(self):
        response = 'Working {not json} then {"result":"ok"} done'
        self.assertEqual({"result": "ok"}, self.select_result(response))

    def test_searches_arrays_and_envelopes(self):
        response = '[{"event":"tool"},{"output":{"result":"ok"}}]'
        self.assertEqual({"result": "ok"}, self.select_result(response))

    def test_searches_fenced_array_after_tool_output(self):
        response = '{"event":"tool"}\n```json\n[{"output":{"result":"ok"}}]\n```'
        self.assertEqual({"result": "ok"}, self.select_result(response))

    def test_skips_fenced_tool_object_before_result(self):
        response = '```json\n{"event":"tool"}\n```\nFinal: {"result":"ok"}'
        self.assertEqual({"result": "ok"}, self.select_result(response))

    def test_preserves_braces_inside_strings(self):
        self.assertEqual(
            [{"value": "brace } in string"}, [1, 2]],
            list(iter_json_values('x {"value":"brace } in string"} y [1,2]')),
        )

    def test_returns_none_without_matching_object(self):
        self.assertIsNone(self.select_result('text {"event":"tool"}'))


if __name__ == "__main__":
    unittest.main()