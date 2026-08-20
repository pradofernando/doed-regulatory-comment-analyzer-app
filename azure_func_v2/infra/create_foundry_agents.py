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


def main() -> int:
    parser = argparse.ArgumentParser(description="Create Azure AI Foundry prompt-agent versions using Entra auth.")
    parser.add_argument("--project-endpoint", required=True)
    parser.add_argument("--definitions-file", required=True)
    args = parser.parse_args()

    definitions = _load_definitions(Path(args.definitions_file))
    credential = AzureCliCredential()
    client = AIProjectClient(endpoint=args.project_endpoint, credential=credential, allow_preview=True)

    created: dict[str, dict] = {}
    errors: dict[str, str] = {}

    for definition in definitions:
        env_prefix = definition["env_prefix"]

        try:
            result = client.agents.create_version(
                agent_name=definition["agent_name"],
                definition=PromptAgentDefinition(
                    model=definition["model"],
                    instructions=definition["instructions"],
                ),
                description=definition.get("description"),
            )

            created[env_prefix] = {
                "Id": _coalesce(getattr(result, "id", None), definition["agent_name"]),
                "Name": _coalesce(getattr(result, "name", None), definition["agent_name"]),
                "Version": str(_coalesce(getattr(result, "version", None), "1")),
                "Model": definition["model"],
            }
        except Exception as exc:  # noqa: BLE001
            errors[env_prefix] = str(exc)

    json.dump({"created": created, "errors": errors}, sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())