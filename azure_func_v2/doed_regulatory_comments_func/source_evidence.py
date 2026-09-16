import hashlib
from html.parser import HTMLParser
from typing import Any, Dict, List, Mapping


MAX_CAPTURE_CHARACTERS = 24000
PASSAGE_CHARACTERS = 2000


class _PlainText(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts: List[str] = []
        self.ignored = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style", "noscript", "template"):
            self.ignored += 1
        elif not self.ignored and tag in ("p", "div", "br", "li", "tr"):
            self.parts.append("\n")

    def handle_endtag(self, tag):
        if tag in ("script", "style", "noscript", "template"):
            self.ignored = max(0, self.ignored - 1)
        elif not self.ignored and tag in ("p", "div", "li", "tr"):
            self.parts.append("\n")

    def handle_data(self, data):
        if not self.ignored:
            self.parts.append(data)


def plain_text(value: str) -> str:
    parser = _PlainText()
    parser.feed(value or "")
    return "\n".join(" ".join(line.split()) for line in "".join(parser.parts).splitlines()).strip()


def new_source(number: int, comment: Mapping[str, Any]) -> Dict[str, Any]:
    return {
        "commentId": comment["comment_id"], "submissionNumber": number,
        "title": str(comment.get("title") or ""), "organization": str(comment.get("organization") or ""),
        "commenter": str(comment.get("commenter_name") or ""),
        "postedAt": comment.get("posted_date") or None, "modifiedAt": comment.get("modified_date") or None,
        "passages": [], "warnings": [], "extractionStatus": "complete", "contentHash": "",
    }


def add_passage(source: Dict[str, Any], text: str, kind: str, title: str, url=None, page=None) -> None:
    if not text or not text.strip():
        return
    remaining = MAX_CAPTURE_CHARACTERS - sum(len(p["text"]) for p in source["passages"])
    captured = text[:max(0, remaining)]
    for offset in range(0, len(captured), PASSAGE_CHARACTERS):
        chunk = captured[offset:offset + PASSAGE_CHARACTERS]
        source["passages"].append({
            "id": f"s{source['submissionNumber']}-p{len(source['passages']) + 1}",
            "text": chunk, "kind": kind, "title": title, "url": url,
            "pageNumber": page if isinstance(page, int) and not isinstance(page, bool) and page > 0 else None,
            "truncated": len(captured) < len(text) and offset + len(chunk) == len(captured),
        })
    if len(captured) < len(text):
        source["warnings"].append(f"Source capture limited to {MAX_CAPTURE_CHARACTERS} characters; omitted text was not analyzed.")


def finish_source(source: Dict[str, Any]) -> Dict[str, Any]:
    source["warnings"] = list(dict.fromkeys(source["warnings"]))
    source["extractionStatus"] = "unreadable" if not source["passages"] else "partial" if source["warnings"] else "complete"
    source["contentHash"] = hashlib.sha256("\n".join(p["text"] for p in source["passages"]).encode("utf-8")).hexdigest()
    return source


def validate_evidence(source: Dict[str, Any], categorization: Dict[str, Any]) -> None:
    passages = {p["id"]: p["text"] for p in source["passages"]}
    raw = categorization.get("evidence", [])
    if not isinstance(raw, list):
        raw = [raw]
    verified = []
    for item in raw:
        if isinstance(item, Mapping):
            source_id, quote = item.get("source_id"), item.get("quote")
            if isinstance(source_id, str) and isinstance(quote, str) and quote and quote in passages.get(source_id, ""):
                if item not in verified:
                    verified.append({"source_id": source_id, "quote": quote})
                continue
        source["warnings"].append("An unsupported quotation was rejected.")
    if "evidence" not in categorization:
        phrases = categorization.get("key_phrases", [])
        if isinstance(phrases, list):
            for quote in phrases:
                if not isinstance(quote, str) or not quote:
                    continue
                match = next((key for key, text in passages.items() if quote in text), None)
                if match:
                    verified.append({"source_id": match, "quote": quote})
    categorization["evidence"] = verified
    source["warnings"] = list(dict.fromkeys(source["warnings"]))


def validate_group_evidence(sources: List[Dict[str, Any]], grouped: Dict[str, Any]) -> None:
    passages = {p["id"]: (s["submissionNumber"], p["text"]) for s in sources for p in s["passages"]}
    for group in grouped.get("categories", grouped.get("theme_groups", [])):
        verified = []
        for evidence in group.get("evidence") or []:
            if not isinstance(evidence, Mapping):
                continue
            source_id = evidence.get("source_id")
            match = passages.get(source_id) if isinstance(source_id, str) else None
            quote = evidence.get("quote")
            if (match and match[0] in group.get("submission_numbers", [])
                    and evidence.get("finding") in group.get("common_arguments", [])
                    and isinstance(quote, str) and quote and quote in match[1]):
                verified.append(dict(evidence))
        group["evidence"] = verified
