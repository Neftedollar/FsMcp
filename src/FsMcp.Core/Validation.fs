namespace FsMcp.Core

/// Validation errors for domain type construction.
type ValidationError =
    | EmptyValue of fieldName: string
    | InvalidFormat of fieldName: string * value: string * expected: string
    | DuplicateEntry of entryType: string * name: string

/// Validated identifier types with smart constructors.
/// Each type is a single-case DU that guarantees validity at construction time.
module Validation =

    // ───────────────────────────────────────────────────────
    //  Helper
    // ───────────────────────────────────────────────────────

    [<AutoOpen>]
    module private Helpers =
        let inline isNullOrWhiteSpace (s: string) =
            System.String.IsNullOrWhiteSpace s

    // ───────────────────────────────────────────────────────
    //  ToolName
    // ───────────────────────────────────────────────────────

    /// A validated, non-empty, trimmed tool name.
    type ToolName = private | ToolName of string

    module ToolName =
        /// Extract the raw string value.
        let value (ToolName v) = v

        /// Create a ToolName from a string. Returns Error for null, empty, or whitespace-only strings.
        /// The value is trimmed before storage.
        let create (s: string) : Result<ToolName, ValidationError> =
            if isNullOrWhiteSpace s then
                Error (EmptyValue "ToolName")
            else
                Ok (ToolName (s.Trim()))

    // ───────────────────────────────────────────────────────
    //  ResourceUri
    // ───────────────────────────────────────────────────────

    /// A validated URI string.
    type ResourceUri = private | ResourceUri of string

    module ResourceUri =
        /// Extract the raw string value.
        let value (ResourceUri v) = v

        /// Create a ResourceUri from a string. Returns Error for null, empty, or invalid URI strings.
        /// The URI must be an absolute URI with a scheme.
        let create (s: string) : Result<ResourceUri, ValidationError> =
            if isNullOrWhiteSpace s then
                Error (EmptyValue "ResourceUri")
            else
                match System.Uri.TryCreate(s, System.UriKind.Absolute) with
                | true, uri when not (System.String.IsNullOrEmpty uri.Scheme)
                              && s.Contains("://") ->
                    Ok (ResourceUri s)
                | _ ->
                    Error (InvalidFormat ("ResourceUri", s, "valid absolute URI with scheme (e.g., https://example.com/resource)"))

    // ───────────────────────────────────────────────────────
    //  PromptName
    // ───────────────────────────────────────────────────────

    /// A validated, non-empty, trimmed prompt name.
    type PromptName = private | PromptName of string

    module PromptName =
        /// Extract the raw string value.
        let value (PromptName v) = v

        /// Create a PromptName from a string. Returns Error for null, empty, or whitespace-only strings.
        /// The value is trimmed before storage.
        let create (s: string) : Result<PromptName, ValidationError> =
            if isNullOrWhiteSpace s then
                Error (EmptyValue "PromptName")
            else
                Ok (PromptName (s.Trim()))

    // ───────────────────────────────────────────────────────
    //  MimeType
    // ───────────────────────────────────────────────────────

    /// A validated MIME type string (e.g., "text/plain", "application/json").
    /// Empty or null defaults to "application/octet-stream".
    type MimeType = private | MimeType of string

    module MimeType =
        /// The default MIME type used when none is provided.
        let defaultMimeType = "application/octet-stream"

        /// Extract the raw string value.
        let value (MimeType v) = v

        /// Create a MimeType from a string.
        /// Null or empty string defaults to "application/octet-stream".
        /// Non-empty values must contain a slash separating type and subtype.
        let create (s: string) : Result<MimeType, ValidationError> =
            if System.String.IsNullOrEmpty s || isNull s then
                Ok (MimeType defaultMimeType)
            else
                // Basic MIME validation: must have type/subtype format
                let trimmed = s.Trim()
                // Handle parameters (e.g., "text/html; charset=utf-8")
                let mainPart =
                    match trimmed.IndexOf(';') with
                    | -1 -> trimmed
                    | idx -> trimmed.Substring(0, idx).Trim()
                match mainPart.IndexOf('/') with
                | -1 ->
                    Error (InvalidFormat ("MimeType", s, "valid MIME type (e.g., text/plain, application/json)"))
                | slashIdx ->
                    let typePart = mainPart.Substring(0, slashIdx).Trim()
                    let subtypePart = mainPart.Substring(slashIdx + 1).Trim()
                    if System.String.IsNullOrEmpty typePart || System.String.IsNullOrEmpty subtypePart then
                        Error (InvalidFormat ("MimeType", s, "valid MIME type with non-empty type and subtype"))
                    else
                        Ok (MimeType trimmed)

    // ───────────────────────────────────────────────────────
    //  ServerName
    // ───────────────────────────────────────────────────────

    /// A validated, non-empty server name.
    type ServerName = private | ServerName of string

    module ServerName =
        /// Extract the raw string value.
        let value (ServerName v) = v

        /// Create a ServerName from a string. Returns Error for null, empty, or whitespace-only strings.
        /// The value is trimmed before storage.
        let create (s: string) : Result<ServerName, ValidationError> =
            if isNullOrWhiteSpace s then
                Error (EmptyValue "ServerName")
            else
                Ok (ServerName (s.Trim()))

    // ───────────────────────────────────────────────────────
    //  ServerVersion
    // ───────────────────────────────────────────────────────

    /// A validated, non-empty server version.
    type ServerVersion = private | ServerVersion of string

    module ServerVersion =
        /// Extract the raw string value.
        let value (ServerVersion v) = v

        /// Create a ServerVersion from a string. Returns Error for null, empty, or whitespace-only strings.
        /// The value is trimmed before storage.
        let create (s: string) : Result<ServerVersion, ValidationError> =
            if isNullOrWhiteSpace s then
                Error (EmptyValue "ServerVersion")
            else
                Ok (ServerVersion (s.Trim()))
