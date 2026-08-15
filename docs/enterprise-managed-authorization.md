---
title: Enterprise-Managed Authorization
category: Guides
categoryindex: 1
index: 1
---

# Enterprise-Managed Authorization

FsMcp.Client 2.0 supports the MCP enterprise ID-JAG flow through validated,
opaque F# values. An application supplies an enterprise identity-token callback;
FsMcp exchanges that assertion, caches the resulting access token, and adds a
Bearer header only to the configured MCP resource origin.

## Configure the identity provider

Use either an OIDC issuer or a direct token endpoint. Production constructors
require HTTPS. The `ForDevelopment` forms accept plain HTTP only for loopback
URIs.

```fsharp
open System
open System.Threading.Tasks
open FsMcp.Client

let identityProvider =
    EnterpriseIdentityProvider.fromIssuer
        (Uri "https://identity.example.com/")
        "identity-client"
    |> Result.bind (EnterpriseIdentityProvider.withScopes [ "openid" ])
```

`withClientSecret` adds confidential-client authentication. Opaque provider and
option values never render their secrets from `ToString()`.

## Create one authorization session

The resource is the protected MCP identifier. With the Microsoft MCP SDK 1.4.1,
the authorization-server value must be an authority-root issuer: `/` as the
path and no query. This is a current SDK integration constraint, not a general
OAuth requirement.

```fsharp
let createAuthorization identityProvider =
    let identityTokenProvider context cancellationToken =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            // Acquire an ID token for the signed-in enterprise user. The
            // callback receives the validated resource and authorization server.
            return! acquireEnterpriseIdToken context cancellationToken
        }

    EnterpriseManagedAuthorizationOptions.create
        (Uri "https://mcp.example.com/mcp")
        (Uri "https://authorization.example.com/")
        "mcp-client"
        identityProvider
        identityTokenProvider
    |> Result.map EnterpriseManagedAuthorization.create
```

The safe constructor owns a redirect-disabled, response-bounded token-exchange
`HttpClient`. Scope an authorization session to one signed-in user, resource,
and authorization server. It may be shared by several matching MCP clients;
dispose it only after every client using it has disconnected.

Options are immutable and pipeline-friendly:

- `withMcpClientSecret` configures confidential MCP-client authentication;
- `withScopes` sets validated OAuth scope tokens;
- `withRefreshSkew` reserves token lifetime for proactive refresh;
- `withUnspecifiedTokenLifetime` bounds caching when `expires_in` is absent;
- `withTokenAcquisitionTimeout` sets the public acquisition wait bound;
- `withMaxRetryBodyBytes` sets the request replay bound, up to 4 MiB.

`EnterpriseManagedAuthorization.createWithLogger loggerFactory options` enables
redacted operational diagnostics. It never logs tokens, secrets, OAuth bodies,
or endpoint queries.

## Connect the client

```fsharp
let connect authorization =
    let config = {
        Transport = ClientTransport.http "https://mcp.example.com/mcp"
        Name = "enterprise-client"
        ShutdownTimeout = Some(TimeSpan.FromSeconds 10.0)
    }

    McpClient.connectEnterpriseManaged config authorization
```

Enterprise-managed authorization works only with `HttpEndpoint`. A static
`Authorization` header and an explicit `Host` header are rejected. The endpoint
must have the same scheme, IDN host, and port as the configured resource.

## Runtime behavior and safety bounds

- access-token acquisition is single-flight per authorization session;
- cached tokens refresh before expiry using the configured skew;
- the first `401` evicts the rejected token and retries once with a fresh token;
- a known or bounded request body can be replayed; a larger or unbounded body is
  sent once and is never replayed;
- access tokens must use the RFC 6750 Bearer `b64token` character set, have no
  surrounding whitespace or controls, and remain within 65,536 characters;
- token metadata is fetched only from the exact standard well-known paths;
- metadata must return the exact configured issuer and a trusted token endpoint;
- automatic redirects, cookies, and decompression are disabled for token
  exchange, and every response is bounded to 1 MiB;
- cancellation and disposal release public waiters even if a dependency ignores
  cancellation; the underlying single-flight slot remains occupied until that
  work settles;
- failures exposed by enterprise-connected MCP operations are typed and redacted.

Credential acquisition failure never falls back to an anonymous MCP request.

## Protect the resource server

Client-side authorization does not secure an MCP server. The ASP.NET Core host
must authenticate access tokens, validate the expected issuer and audience,
require authorization on the MCP endpoint, and reject unauthenticated requests.

For stateful Streamable HTTP, configure authentication to produce a stable
identity claim for every user: `ClaimTypes.NameIdentifier`, `sub`, or UPN. The
MCP SDK uses that identity to bind a session to its owner. Reject identities
without one of those claims; otherwise unrelated principals can collapse to the
same missing session identity.

`HttpServer.run` is intentionally only a convenience host. It does not choose an
issuer, validate access tokens, or create an authorization policy.

## Supported profile boundary

The current wrapper covers the official SDK 1.4.1 OIDC ID-token path with
`client_id` and optional `client_secret_post` authentication. It does not implement:

- SAML assertions or a SAML-to-refresh-token flow;
- `client_secret_basic` client authentication;
- `private_key_jwt` client authentication;
- DPoP or mTLS sender-constrained tokens;
- dynamic client registration or token introspection;
- Client ID Metadata Documents;
- automatic discovery or validation of
  `authorization_grant_profiles_supported`.

Configure the resource and authorization-server issuer explicitly, and verify
out of band that the authorization server supports
`urn:ietf:params:oauth:grant-profile:id-jag`. The API intentionally does not
claim unsupported branches of the broader stable profile.
