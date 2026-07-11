"""STS2 MCP server package."""

from __future__ import annotations

from typing import Any

__all__ = ["Sts2ApiError", "Sts2Client", "Sts2HandoffService", "create_server"]


def __getattr__(name: str) -> Any:
    if name in {"Sts2ApiError", "Sts2Client"}:
        from .client import Sts2ApiError, Sts2Client

        return {"Sts2ApiError": Sts2ApiError, "Sts2Client": Sts2Client}[name]
    if name == "Sts2HandoffService":
        from .handoff import Sts2HandoffService

        return Sts2HandoffService
    if name == "create_server":
        from .server import create_server

        return create_server
    raise AttributeError(name)
