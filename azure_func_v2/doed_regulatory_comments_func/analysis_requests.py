import datetime
import re
import uuid
from typing import Any, Dict, Iterable, Mapping, Optional


SCHEMA_VERSION = 1
MAX_BATCH_SIZE = 25
MAX_COMMENTS = 1000

_DOCUMENT_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
_MODEL_KEYS = ("categorization", "grouping", "validation")
_REQUEST_KEYS = {"documentId", "commentIds", "maxComments", "batchSize", "models", "runValidation"}


class AnalysisRequestValidationError(ValueError):
    pass


def create_analysis_request(
    payload: Mapping[str, Any],
    *,
    trigger_source: str,
    default_document_id: str,
    default_batch_size: int,
    default_max_comments: Optional[int],
    default_models: Mapping[str, Optional[str]],
    allowed_models: Iterable[str] = (),
    run_id: Optional[str] = None,
    requested_at: Optional[datetime.datetime] = None,
) -> Dict[str, Any]:
    unknown_keys = set(payload) - _REQUEST_KEYS
    if unknown_keys:
        raise AnalysisRequestValidationError(
            f"Unsupported request properties: {', '.join(sorted(unknown_keys))}."
        )

    document_id = payload.get("documentId", default_document_id)
    if not isinstance(document_id, str) or not _DOCUMENT_ID_PATTERN.fullmatch(document_id):
        raise AnalysisRequestValidationError(
            "documentId must be 1-64 characters using letters, numbers, periods, underscores, or hyphens."
        )

    batch_size = _validate_optional_int(
        payload.get("batchSize", default_batch_size),
        "batchSize",
        minimum=1,
        maximum=MAX_BATCH_SIZE,
        allow_none=False,
    )
    max_comments = _validate_optional_int(
        payload.get("maxComments", default_max_comments),
        "maxComments",
        minimum=1,
        maximum=MAX_COMMENTS,
        allow_none=True,
    )
    comment_ids = payload.get("commentIds", [])
    if not isinstance(comment_ids, list) or len(comment_ids) > MAX_COMMENTS:
        raise AnalysisRequestValidationError(
            f"commentIds must be an array containing at most {MAX_COMMENTS} IDs."
        )
    if any(not isinstance(comment_id, str) or not comment_id.strip() for comment_id in comment_ids):
        raise AnalysisRequestValidationError("Every commentIds value must be a non-empty string.")
    normalized_comment_ids = list(dict.fromkeys(comment_id.strip() for comment_id in comment_ids))

    requested_models = payload.get("models", {})
    if not isinstance(requested_models, Mapping):
        raise AnalysisRequestValidationError("models must be a JSON object.")
    unknown_model_keys = set(requested_models) - set(_MODEL_KEYS)
    if unknown_model_keys:
        raise AnalysisRequestValidationError(
            f"Unsupported model properties: {', '.join(sorted(unknown_model_keys))}."
        )

    model_allowlist = {model.strip() for model in allowed_models if model and model.strip()}
    models: Dict[str, Optional[str]] = {}
    for key in _MODEL_KEYS:
        default_model = default_models.get(key)
        model = requested_models.get(key, default_model)
        if model is not None and (not isinstance(model, str) or not model.strip()):
            raise AnalysisRequestValidationError(f"models.{key} must be a non-empty string.")
        normalized_model = model.strip() if isinstance(model, str) else None
        if key in requested_models and normalized_model not in model_allowlist:
            raise AnalysisRequestValidationError(
                f"models.{key} is not an approved model deployment."
            )
        models[key] = normalized_model

    run_validation = payload.get("runValidation", bool(default_models.get("validation")))
    if not isinstance(run_validation, bool):
        raise AnalysisRequestValidationError("runValidation must be true or false.")

    timestamp = requested_at or datetime.datetime.now(datetime.timezone.utc)
    if timestamp.tzinfo is None:
        timestamp = timestamp.replace(tzinfo=datetime.timezone.utc)

    return {
        "schemaVersion": SCHEMA_VERSION,
        "runId": run_id or str(uuid.uuid4()),
        "triggerSource": trigger_source,
        "requestedAt": timestamp.astimezone(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
        "documentId": document_id,
        "commentIds": normalized_comment_ids,
        "maxComments": max_comments,
        "batchSize": batch_size,
        "models": models,
        "runValidation": run_validation,
    }


def _validate_optional_int(
    value: Any,
    name: str,
    *,
    minimum: int,
    maximum: int,
    allow_none: bool,
) -> Optional[int]:
    if value is None and allow_none:
        return None
    if isinstance(value, bool) or not isinstance(value, int):
        raise AnalysisRequestValidationError(f"{name} must be an integer.")
    if value < minimum or value > maximum:
        raise AnalysisRequestValidationError(
            f"{name} must be between {minimum} and {maximum}."
        )
    return value