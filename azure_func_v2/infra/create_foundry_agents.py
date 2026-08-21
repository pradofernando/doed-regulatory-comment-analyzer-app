import argparse
import json
import sys
from pathlib import Path

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import PromptAgentDefinition
from azure.identity import AzureCliCredential


def _load_definitions(path: Path) -> list[dict]:
    with path.open("r", encoding="utf-8-sig") as handle:
        data = json.load(handle)

    if isinstance(data, dict):
        return [data]

    if not isinstance(data, list):
        raise ValueError("Definitions file must contain a JSON array.")

    return data


def _coalesce(value, default):
    if value is None:
        return default
    if isinstance(value, str) and not value.strip():
        return default
    return value


def _version_sort_key(version: object) -> tuple[int, int | str]:
    normalized = str(version)
    return (1, int(normalized)) if normalized.isdigit() else (0, normalized)


def _find_latest_matching_version(
    client,
    agent_name: str,
    model: str,
    instructions: str,
):
    matching_versions = []
    for version in client.agents.list_versions(agent_name, include_drafts=False):
        definition = getattr(version, "definition", None)
        published_model = _coalesce(getattr(definition, "model", None), "")
        published_instructions = _coalesce(getattr(definition, "instructions", None), "")
        if published_model == model and published_instructions == instructions:
            matching_versions.append(version)

    return max(
        matching_versions,
        key=lambda version: _version_sort_key(getattr(version, "version", "")),
        default=None,
    )


def _agent_result(result, agent_name: str, requested_model: str) -> dict:
    published_model = _coalesce(
        getattr(getattr(result, "definition", None), "model", None),
        "",
    )
    if published_model != requested_model:
        raise RuntimeError(
            f"Agent version was published with model '{published_model}' "
            f"instead of '{requested_model}'."
        )

    return {
        "Id": _coalesce(getattr(result, "id", None), agent_name),
        "Name": _coalesce(getattr(result, "name", None), agent_name),
        "Version": str(_coalesce(getattr(result, "version", None), "1")),
        "Model": published_model,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Create Azure AI Foundry prompt-agent versions using Entra auth.")
    parser.add_argument("--project-endpoint", required=True)
    parser.add_argument("--definitions-file", required=True)
    parser.add_argument("--required-env-prefix", action="append", default=[])
    args = parser.parse_args()

    definitions = _load_definitions(Path(args.definitions_file))
    defined_prefixes = {definition.get("env_prefix") for definition in definitions}
    missing_prefixes = sorted(set(args.required_env_prefix) - defined_prefixes)
    if missing_prefixes:
        parser.error(
            "Definitions file is missing required env prefixes: "
            + ", ".join(missing_prefixes)
        )

    credential = AzureCliCredential()
    client = AIProjectClient(endpoint=args.project_endpoint, credential=credential, allow_preview=True)

    created: dict[str, dict] = {}
    errors: dict[str, str] = {}

    for definition in definitions:
        env_prefix = definition["env_prefix"]

        try:
            result = _find_latest_matching_version(
                client,
                definition["agent_name"],
                definition["model"],
                definition["instructions"],
            )
            if result is None:
                result = client.agents.create_version(
                    agent_name=definition["agent_name"],
                    definition=PromptAgentDefinition(
                        model=definition["model"],
                        instructions=definition["instructions"],
                    ),
                    description=definition.get("description"),
                )

            created[env_prefix] = _agent_result(
                result,
                definition["agent_name"],
                definition["model"],
            )
        except Exception as exc:  # noqa: BLE001
            errors[env_prefix] = str(exc)

    json.dump({"created": created, "errors": errors}, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())