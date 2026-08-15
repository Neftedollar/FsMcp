---
title: Middleware Migration Guide
category: Guides
categoryindex: 1
index: 0
---

# Middleware Migration Guide

FsMcp 1.x exposed a parallel `McpMiddleware` pipeline, but the official SDK
transport bridge never invoked it. FsMcp 2.0 therefore marks the legacy
middleware surface obsolete and rejects non-empty legacy middleware
configuration. This is a breaking, fail-closed correction: middleware that does
not run must not appear to protect or observe requests.

## Protocol request interception

Register FsMcp into a caller-owned service collection and use the official
ModelContextProtocol SDK request-filter APIs. Filters run inside the actual SDK
request path and receive its request context and cancellation token.

Use request filters for protocol-aware concerns such as request logging,
validation, metrics, or access checks that need MCP method information. Keep
secret values and full request bodies out of logs.

## Streamable HTTP

For HTTP servers, compose standard ASP.NET Core middleware before mapping the
MCP endpoint. Configure authentication and authorization in the host, then
require authorization on the mapped MCP endpoint. Enterprise-managed
authorization on the client does not protect the resource server by itself.

Use ASP.NET Core for transport concerns such as forwarded headers, request-size
limits, rate limiting, authentication, authorization, and endpoint policy.

## Stdio

Stdio has no ASP.NET Core request pipeline. Use SDK request filters and
caller-owned dependency injection. Start it with `Server.run`; there is no
transport selector in the `mcpServer { }` computation expression.

## Legacy helpers

`Middleware.compose`, `Middleware.pipeline`, `ValidationMiddleware`, and the
legacy telemetry middleware remain only as obsolete compatibility surfaces.
Do not rely on them for runtime behavior in 2.0. Migrate to the official SDK
filter/DI path or ASP.NET Core middleware as appropriate.
