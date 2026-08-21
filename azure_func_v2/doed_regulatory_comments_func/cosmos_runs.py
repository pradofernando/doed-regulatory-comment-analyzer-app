import datetime
import gzip
import json
import os
from typing import Any, Dict, List, Mapping, Optional

from azure.cosmos import CosmosClient
from azure.cosmos.exceptions import CosmosHttpResponseError, CosmosResourceNotFoundError
from azure.core.exceptions import ResourceExistsError
from azure.identity import DefaultAzureCredential
from azure.storage.blob import BlobServiceClient


FRONTEND_SCHEMA_VERSION = 2
DEFAULT_PAYLOAD_OFFLOAD_THRESHOLD_BYTES = 512 * 1024

_STANCE_ALIASES = {
    "support": "supportive",
    "supportive": "supportive",
    "oppose": "opposing",
    "opposed": "opposing",
    "opposing": "opposing",
    "oppositional": "opposing",
    "neutral": "neutral",
    "procedural": "neutral",
    "mixed": "mixed",
}

_SENTIMENT_LABELS = {
    "supportive": "supportive",
    "opposing": "oppositional",
    "neutral": "neutral",
    "mixed": "mixed",
}


def utc_now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def derive_overall_sentiment(analysis: Mapping[str, Any]) -> Optional[str]:
    totals = {stance: 0.0 for stance in _SENTIMENT_LABELS}
    categories = analysis.get(
        "categories",
        analysis.get("theme_groups", analysis.get("themeGroups", [])),
    )
    if not isinstance(categories, list):
        return None

    for category in categories:
        if not isinstance(category, Mapping):
            continue
        distribution = category.get(
            "stance_distribution",
            category.get("stanceDistribution", {}),
        )
        if not isinstance(distribution, Mapping):
            continue
        for raw_stance, raw_count in distribution.items():
            stance = _STANCE_ALIASES.get(str(raw_stance).strip().lower())
            if stance is None or isinstance(raw_count, bool) or not isinstance(raw_count, (int, float)):
                continue
            if raw_count > 0:
                totals[stance] += raw_count

    total = sum(totals.values())
    if total <= 0:
        return None

    largest = max(totals.values())
    leaders = [stance for stance, count in totals.items() if count == largest]
    if len(leaders) != 1 or largest <= total / 2:
        return "mixed"

    dominant = leaders[0]
    label = _SENTIMENT_LABELS[dominant]
    return label if largest == total else f"mostly {label}"


def build_job_document(
    request: Mapping[str, Any],
    status: str,
    *,
    started_at: Optional[str] = None,
    completed_at: Optional[str] = None,
    error_message: Optional[str] = None,
) -> Dict[str, Any]:
    return {
        "id": request["runId"],
        "type": "analysisRunJob",
        "schemaVersion": request["schemaVersion"],
        "status": status,
        "triggerSource": request["triggerSource"],
        "requestedAt": request["requestedAt"],
        "startedAt": started_at,
        "completedAt": completed_at,
        "documentId": request["documentId"],
        "documentIdNormalized": request["documentId"].strip().upper(),
        "effectiveSettings": {
            "commentIds": request.get("commentIds", []),
            "maxComments": request["maxComments"],
            "batchSize": request["batchSize"],
            "models": request["models"],
            "runValidation": request["runValidation"],
        },
        "errorMessage": error_message,
    }


def build_analysis_document(
    request: Mapping[str, Any],
    result: Mapping[str, Any],
    *,
    started_at: str,
    completed_at: str,
) -> Dict[str, Any]:
    grouped = result.get("groupedAnalysis")
    if not isinstance(grouped, Mapping):
        grouped = {}

    source_groups = grouped.get("theme_groups", grouped.get("categories", []))
    if not isinstance(source_groups, list):
        source_groups = []

    categorizations = [
        _map_categorization(item)
        for item in result.get("categorizations", [])
        if isinstance(item, Mapping)
    ]
    theme_groups = [
        _map_theme_group(item, position)
        for position, item in enumerate(source_groups)
        if isinstance(item, Mapping)
    ]

    patterns = grouped.get("patterns", [])
    if not isinstance(patterns, list):
        patterns = []
    recommendations = grouped.get("recommendations", [])
    if not isinstance(recommendations, list):
        recommendations = []
    if not recommendations:
        recommendations = [
            item["recommendations"]
            for item in source_groups
            if isinstance(item, Mapping)
            and isinstance(item.get("recommendations"), str)
            and item["recommendations"].strip()
        ]

    derived_sentiment = derive_overall_sentiment(grouped)

    return {
        "id": request["runId"],
        "type": "analysisRun",
        "schemaVersion": FRONTEND_SCHEMA_VERSION,
        "status": "succeeded",
        "triggerSource": request["triggerSource"],
        "effectiveSettings": build_job_document(request, "succeeded")["effectiveSettings"],
        "sessionName": None,
        "documentId": request["documentId"],
        "documentIdNormalized": request["documentId"].strip().upper(),
        "startedAt": started_at,
        "completedAt": completed_at,
        "batchSize": request["batchSize"],
        "totalComments": result["totalComments"],
        "succeeded": True,
        "errorMessage": None,
        "overallSummary": grouped.get("overall_summary", grouped.get("overall_assessment")),
        "overallSentiment": derived_sentiment or grouped.get("overall_sentiment"),
        "patterns": patterns,
        "recommendations": recommendations,
        "followUpThreadId": None,
        "payloadBlobName": None,
        "categorizations": categorizations,
        "themeGroups": theme_groups,
        "followUpHistory": [],
    }


def build_failed_analysis_document(
    request: Mapping[str, Any],
    *,
    started_at: str,
    completed_at: str,
    error_message: str,
) -> Dict[str, Any]:
    document = build_analysis_document(
        request,
        {"totalComments": 0, "categorizations": [], "groupedAnalysis": {}},
        started_at=started_at,
        completed_at=completed_at,
    )
    document.update(status="failed", succeeded=False, errorMessage=error_message)
    return document


def build_summary_document(document: Mapping[str, Any]) -> Dict[str, Any]:
    return {
        "id": document["id"],
        "type": "analysisRunSummary",
        "schemaVersion": FRONTEND_SCHEMA_VERSION,
        "sessionName": document.get("sessionName"),
        "documentId": document["documentId"],
        "documentIdNormalized": document["documentIdNormalized"],
        "startedAt": document["startedAt"],
        "completedAt": document["completedAt"],
        "totalComments": document["totalComments"],
        "themeCount": len(document["themeGroups"]),
        "succeeded": document["succeeded"],
        "errorMessage": document.get("errorMessage"),
        "overallSentiment": document.get("overallSentiment"),
    }


def serialize_categorization_payload(document: Mapping[str, Any]) -> bytes:
    payload = {
        "schemaVersion": 1,
        "categorizations": [
            {
                "submissionNumber": item["submissionNumber"],
                "rawResponse": item["rawResponse"],
                "parsedJson": item["parsedJson"],
            }
            for item in document.get("categorizations", [])
        ],
    }
    return gzip.compress(
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
        compresslevel=9,
    )


def _map_categorization(item: Mapping[str, Any]) -> Dict[str, Any]:
    parsed = item.get("categorization", {})
    raw_response = parsed if isinstance(parsed, str) else json.dumps(parsed, ensure_ascii=False)
    parsed_json = raw_response if not isinstance(parsed, str) else "{}"
    return {
        "submissionNumber": int(item.get("submission_number", 0)),
        "commentId": str(item.get("comment_id", "")),
        "rawResponse": raw_response,
        "parsedJson": parsed_json,
        "textSource": str(item.get("text_source", "inline")),
        "attachmentsExtracted": int(item.get("attachments_extracted", 0)),
    }


def _map_theme_group(item: Mapping[str, Any], position: int) -> Dict[str, Any]:
    return {
        "position": position,
        "groupName": str(item.get("group_name", "")),
        "groupDescription": str(item.get("group_description", item.get("category_summary", ""))),
        "count": int(item.get("count", item.get("comment_count", 0))),
        "submissionNumbers": item.get("submission_numbers", []),
        "stanceDistribution": item.get("stance_distribution", {}),
        "commonArguments": item.get("common_arguments", []),
    }


class CosmosRunStore:
    def __init__(
        self,
        endpoint: str,
        database_name: str,
        runs_container_name: str,
        summaries_container_name: str,
        storage_account_name: Optional[str] = None,
        payload_container_name: str = "analysis-run-payloads",
        payload_offload_threshold_bytes: int = DEFAULT_PAYLOAD_OFFLOAD_THRESHOLD_BYTES,
    ) -> None:
        credential = DefaultAzureCredential()
        client = CosmosClient(endpoint, credential=credential)
        database = client.get_database_client(database_name)
        self._runs = database.get_container_client(runs_container_name)
        self._summaries = database.get_container_client(summaries_container_name)
        self._payload_container = None
        if storage_account_name:
            blob_service = BlobServiceClient(
                account_url=f"https://{storage_account_name}.blob.core.windows.net",
                credential=credential,
            )
            self._payload_container = blob_service.get_container_client(payload_container_name)
        self._payload_offload_threshold_bytes = payload_offload_threshold_bytes

    @classmethod
    def from_environment(cls) -> Optional["CosmosRunStore"]:
        endpoint = os.environ.get("COSMOS_ENDPOINT")
        if not endpoint:
            return None
        return cls(
            endpoint,
            os.environ.get("COSMOS_DATABASE_NAME", "doed-regulatory-comments"),
            os.environ.get("COSMOS_RUNS_CONTAINER_NAME", "analysis-runs"),
            os.environ.get("COSMOS_SUMMARIES_CONTAINER_NAME", "analysis-run-summaries"),
            os.environ.get("AZURE_STORAGE_ACCOUNT_NAME"),
            os.environ.get("ANALYSIS_PAYLOAD_CONTAINER_NAME", "analysis-run-payloads"),
            int(os.environ.get(
                "ANALYSIS_PAYLOAD_OFFLOAD_THRESHOLD_BYTES",
                str(DEFAULT_PAYLOAD_OFFLOAD_THRESHOLD_BYTES),
            )),
        )

    def save_job(self, document: Mapping[str, Any]) -> None:
        self._runs.upsert_item(dict(document))

    def try_start(self, run_id: str, started_at: str) -> bool:
        try:
            self._runs.patch_item(
                item=run_id,
                partition_key=run_id,
                patch_operations=[
                    {"op": "replace", "path": "/status", "value": "running"},
                    {"op": "replace", "path": "/startedAt", "value": started_at},
                    {"op": "replace", "path": "/completedAt", "value": None},
                    {"op": "replace", "path": "/errorMessage", "value": None},
                ],
                filter_predicate="FROM c WHERE c.status = 'queued' OR c.status = 'failed'",
            )
            return True
        except CosmosHttpResponseError as error:
            if error.status_code in (404, 412):
                return False
            raise

    def save_analysis(self, document: Mapping[str, Any]) -> None:
        stored_document = dict(document)
        stored_document["categorizations"] = [
            dict(item) for item in document.get("categorizations", [])
        ]
        self._offload_payload_if_needed(stored_document)
        self._runs.upsert_item(stored_document)
        self._summaries.upsert_item(build_summary_document(stored_document))

    def _offload_payload_if_needed(self, document: Dict[str, Any]) -> None:
        payload_size = sum(
            len(item.get("rawResponse", "").encode("utf-8"))
            + len(item.get("parsedJson", "").encode("utf-8"))
            for item in document["categorizations"]
        )
        if payload_size < self._payload_offload_threshold_bytes:
            return
        if self._payload_container is None:
            raise RuntimeError(
                "Analysis payload exceeds the inline Cosmos threshold, but Blob Storage is not configured."
            )

        try:
            self._payload_container.create_container()
        except ResourceExistsError:
            pass

        blob_name = f"analysis-runs/{document['id']}/categorizations.json.gz"
        self._payload_container.upload_blob(
            name=blob_name,
            data=serialize_categorization_payload(document),
            overwrite=True,
        )
        for item in document["categorizations"]:
            item["rawResponse"] = ""
            item["parsedJson"] = "{}"
        document["payloadBlobName"] = blob_name

    def get(self, run_id: str) -> Optional[Dict[str, Any]]:
        try:
            return self._runs.read_item(item=run_id, partition_key=run_id)
        except CosmosResourceNotFoundError:
            return None