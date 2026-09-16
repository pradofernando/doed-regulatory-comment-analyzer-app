import unittest
from types import SimpleNamespace
from unittest.mock import Mock
from azure.core.exceptions import HttpResponseError, ResourceNotFoundError

from create_foundry_agents import build_definition, _find_latest_matching_version


class AgentDefinitionTests(unittest.TestCase):
    def definition(self, **overrides):
        return {
            "env_prefix": "CATEGORIZATION", "model": "synthetic-model",
            "instructions": "Use the supplied comment text.", **overrides,
        }

    def test_no_retrieval_mode_is_explicit(self):
        definition = build_definition(self.definition())
        self.assertEqual([], definition.tools)
        self.assertIn("No retrieval tool is available", definition.instructions)
        self.assertIn("not calibrated accuracy", definition.instructions)

    def test_search_tool_is_attached_only_to_appropriate_roles(self):
        config = {"search_connection_id": "synthetic-connection", "search_index_name": "methodology"}
        for role, expected in (("CATEGORIZATION", 1), ("GROUPING", 1), ("VALIDATION", 0), ("FOLLOWUP", 0)):
            with self.subTest(role=role):
                definition = build_definition(self.definition(env_prefix=role, **config))
                self.assertEqual(expected, len(definition.tools))
        tool = build_definition(self.definition(**config)).tools[0].as_dict()
        self.assertEqual("methodology", tool["azure_ai_search"]["indexes"][0]["index_name"])
        self.assertEqual("simple", tool["azure_ai_search"]["indexes"][0]["query_type"])

    def test_partial_search_configuration_fails(self):
        with self.assertRaises(ValueError):
            build_definition(self.definition(search_connection_id="connection"))
        with self.assertRaises(ValueError):
            build_definition(self.definition(search_index_name="index"))

    def test_version_reuse_checks_tools_and_uses_latest_matching_version(self):
        no_tools = build_definition(self.definition())
        search = build_definition(self.definition(search_connection_id="connection", search_index_name="index"))
        versions = [
            SimpleNamespace(version="2", definition=no_tools),
            SimpleNamespace(version="10", definition=no_tools),
            SimpleNamespace(version="11", definition=search),
        ]
        client = Mock()
        client.agents.list_versions.return_value = versions
        matched = _find_latest_matching_version(client, "agent", no_tools.model, no_tools.instructions, no_tools.tools)
        self.assertEqual("10", matched.version)
        self.assertIsNone(_find_latest_matching_version(client, "agent", no_tools.model, no_tools.instructions, search.tools))

    def test_new_agent_has_no_published_version_to_reuse(self):
        client = Mock()
        client.agents.list_versions.side_effect = ResourceNotFoundError("Agent does not exist yet")
        self.assertIsNone(_find_latest_matching_version(client, "new-agent", "model", "instructions"))

    def test_agent_lookup_does_not_mask_permission_errors(self):
        client = Mock()
        client.agents.list_versions.side_effect = HttpResponseError("Access denied")
        with self.assertRaises(HttpResponseError):
            _find_latest_matching_version(client, "agent", "model", "instructions")


if __name__ == "__main__":
    unittest.main()
