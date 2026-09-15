import json
from collections.abc import Callable, Iterator, Mapping
from typing import Any, Optional

MAX_STRUCTURED_RESPONSE_CHARS = 1_000_000


def iter_json_values(response_text: str) -> Iterator[Any]:
    """Yield complete JSON objects or arrays embedded in arbitrary response text."""
    if len(response_text) > MAX_STRUCTURED_RESPONSE_CHARS:
        response_text = response_text[-MAX_STRUCTURED_RESPONSE_CHARS:]
    decoder = json.JSONDecoder()
    position = 0
    while position < len(response_text):
        starts = [index for token in ("{", "[") if (index := response_text.find(token, position)) >= 0]
        if not starts:
            return
        start = min(starts)
        try:
            value, end = decoder.raw_decode(response_text, start)
        except (json.JSONDecodeError, RecursionError):
            position = start + 1
            continue
        yield value
        position = end


def select_json_object(
    response_text: str,
    predicate: Callable[[Mapping[str, Any]], bool],
) -> Optional[dict[str, Any]]:
    """Select the first embedded object matching a caller-supplied contract."""
    def walk(value: Any) -> Optional[dict[str, Any]]:
        if isinstance(value, dict):
            if predicate(value):
                return value
            for child in value.values():
                match = walk(child)
                if match is not None:
                    return match
        elif isinstance(value, list):
            for child in value:
                match = walk(child)
                if match is not None:
                    return match
        return None

    for value in iter_json_values(response_text):
        match = walk(value)
        if match is not None:
            return match
    return None