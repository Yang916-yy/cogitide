# Headless browser acceptance

`BrowserCheckTool` implements the shared `IAgentTool` contract as `browser_check`. The Host and desktop register this same implementation. It opens an owned, headless Chromium process and a fresh, nonpersistent context for each call. It does not connect to a running browser or import an account profile, cookies, storage state or login credentials.

The tool always requires application approval. Browser visits execute the target page's own scripts and can cause requests to approved origins. This is an application permission and browser routing policy, not an operating system sandbox or a guarantee about every Chromium network mechanism.

## Request and evidence

```json
{
  "url": "http://127.0.0.1:3000/",
  "allowedOrigins": ["http://127.0.0.1:3001"],
  "assertions": [
    { "type": "text", "value": "Ready 就绪" },
    { "type": "selector", "value": "main h1" },
    { "type": "title", "value": "My application" },
    { "type": "status", "value": 200 }
  ],
  "timeoutSeconds": 20,
  "captureScreenshot": true
}
```

The target URL must use HTTP or HTTPS and cannot contain credentials. Its exact origin is automatically allowed. `allowedOrigins` adds at most eight exact HTTP(S) origins, including port; paths, queries, fragments and wildcard hosts are rejected. Approval therefore shows the URL and additional origins that the call can contact. It does not grant access to arbitrary sites through a redirect.

`assertions` accepts at most sixteen checks. `text` uses Playwright's body text containment assertion, including its whitespace normalization; `selector` requires the first matching element of one CSS selector to be visible; `title` uses Playwright's exact title assertion; `status` compares the main navigation's HTTP status. Playwright waits for asynchronous UI updates within the same total time budget. No user supplied JavaScript, click actions, filesystem paths or browser launch options are accepted.

Structured output reports `outcome`, requested/final URL, title, main status, completed assertion results, console messages, page errors, HTTP errors, failed requests, blocked requests, redirects, truncation and duration. A console error, unhandled page exception, HTTP status of 400 or greater, network failure, blocked request or failed assertion makes the check fail. Informational console messages remain evidence without causing failure. Unobserved assertions are never reported as passed. A timeout is explicitly `timed_out`; caller cancellation propagates cancellation after cleanup.

When requested and reached before the deadline, a 1280×720 PNG viewport screenshot is saved beneath the Host's artifact root in a random call directory as `viewport.png`. Only the tool chooses this path. `ToolResult.ArtifactPath` exposes the completed artifact, including for a completed failing check. Partial and cancelled screenshot writes are removed. The tool never saves browser profiles, page source, response bodies or account storage.

## Bounds and navigation policy

Each call has a one to sixty second total budget (default twenty), at most 128 routed requests, at most 100 evidence records per category and at most 2048 characters per evidence field. Screenshot output is limited to eight MiB. Calls through one tool instance are serialized; queued calls remain subject to their own total budget. Context/browser close has a separate bounded cleanup grace before the owned Playwright driver is disposed.

All routed browser HTTP(S) requests are checked against the exact origin set. Service workers, downloads and WebSockets are disabled. WebSocket handlers never connect to the server. Downloads have no output path.

**All HTTP redirects are stopped**, including redirects to an approved origin. Chromium does not offer normal Playwright route interception for every redirect hop. The adapter therefore uses the official SDK's `Route.FetchAsync` with `MaxRedirects = 0` and `MaxRetries = 0`, records the redirect destination, and aborts the original route. Request the intended final URL explicitly in a subsequent approved call. This avoids a custom proxy or CDP stack and prevents a permitted first hop from hiding a later unapproved destination. Relative paths and page URLs otherwise retain normal browser behavior.

Playwright's fetch API buffers responses. The request, evidence and time limits do not claim a hard browser RAM limit or a streaming response byte limit. Pages with long lived streaming responses may hit the total deadline. Nonnetwork `data:`/`blob:` resources retain Chromium's normal behavior; the adapter does not claim OS isolation or a general egress firewall. The check observes events until its owned context closes and does not claim to detect page errors that would occur after that interval.

## Reuse and provenance

The implementation directly reuses **Microsoft.Playwright 1.64.1**, the official .NET SDK. Its NuGet metadata pins [playwright-dotnet revision `8cc0f7b4e67f7a2ceb91a6cb0a5a8e6de6141ae1`](https://github.com/microsoft/playwright-dotnet/tree/8cc0f7b4e67f7a2ceb91a6cb0a5a8e6de6141ae1), under the [MIT license](https://github.com/microsoft/playwright-dotnet/blob/8cc0f7b4e67f7a2ceb91a6cb0a5a8e6de6141ae1/LICENSE). The package bundles the official Playwright driver, whose Apache 2.0 license, `NOTICE` and `ThirdPartyNotices.txt` remain part of the SDK distribution; Chromium is installed separately by the official installer with its own bundled notices. No Playwright source was copied or ported into this module.

Referenced behavior comes from the official [nonpersistent context](https://playwright.dev/dotnet/docs/api/class-browsercontext), [route fetch and redirect controls](https://playwright.dev/dotnet/docs/api/class-route#route-fetch), [WebSocket routing](https://playwright.dev/dotnet/docs/api/class-websocketroute) and [assertion](https://playwright.dev/dotnet/docs/test-assertions) contracts. Project additions are the explicit origin/redirect policy, strict request validation, bounded evidence, approval definition, artifact naming and cancellation ownership. This is an SDK adapter, not a line for line port or a research contribution.

BCL `Uri`, `System.Text.Json`, cancellation, `SemaphoreSlim`, file APIs and `Stopwatch` supply parsing, budgets, serialization and artifact persistence. The browser engine, HTTP handling, DOM selectors, assertion polling and screenshots belong to Playwright. There is no second HTTP client stack, browser protocol client, selector parser or process supervisor.

## Local verification

Use the repository's pinned SDK to restore and build first, then install the browser matching the compiled SDK using the generated script. For example, after a Debug build:

```powershell
pwsh tests/Workbench.Tests/bin/Debug/net11.0/playwright.ps1 install chromium
```

Linux CI uses `install --with-deps chromium`. Installation is explicit and is never performed by an agent tool call. No GUI or paid model call is needed. Run builds and tests serially with other commands sharing `obj/`, and run tests in the background as required by repository instructions.

`BrowserCheckTests` uses real local BCL HTTP fixtures and headless Chromium to cover multilingual assertions, PNG artifacts, console/page errors, HTTP 404, connection failures, additional origins, blocked subresources/WebSockets, forbidden redirect chains, fresh storage, cancellation and total budget behavior. Invalid requests are tested before any browser is launched. These are functional acceptance fixtures; no performance or research benefit is inferred from their passing. Current execution results belong in the integration report rather than being rewritten into this module's history.
