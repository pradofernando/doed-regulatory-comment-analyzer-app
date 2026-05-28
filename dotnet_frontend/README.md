# DoED Regulatory Comments — .NET Frontend

Blazor Server (.NET 9) app that lets you interact with the regulatory comments backend.
By default it talks to the public **Regulations.gov v4 API** (the same API the Python
Azure Function in [`../azure_func`](../azure_func) uses), but the **base URL and API key
can be overridden at runtime from the Settings page** — useful when you want to point
the UI at a custom backend, an APIM gateway, or a local mock.

## Pages

| Route | What it does |
| --- | --- |
| `/` | Landing page with quick links. |
| `/comments` | Form to fetch comments by document or docket ID; shows them in a table. |
| `/comments/{id}` | Single comment detail with full text + attachment links. |
| `/settings` | Override the API base URL, API key, and default document ID. Persists to `App_Data/api-settings.json`. |

## Configure the default API

Defaults are read from `appsettings.json` (`Api` section):

```json
{
  "Api": {
    "BaseUrl": "https://api.regulations.gov/v4",
    "ApiKey": "",
    "DefaultDocumentId": "ED-2025-SCC-0481-0001"
  }
}
```

You can also set the API key via env var (matches the Python project):

```powershell
$env:REGULATIONS_GOV_API_KEY = "your-key"
```

Anything edited on the `/settings` page wins over both of the above and is written
to `App_Data/api-settings.json` next to the running app.

## Run locally

```powershell
cd dotnet_frontend
# Optional: provide the Regulations.gov key
$env:REGULATIONS_GOV_API_KEY = "your-key"

dotnet run --launch-profile http
# App available at http://localhost:5007
```

To use HTTPS instead (with the dev cert):

```powershell
dotnet dev-certs https --trust   # one-time, if you haven't already
dotnet run --launch-profile https
# https://localhost:7018
```

## Swap the API at runtime

1. Start the app.
2. Open **Settings** in the sidebar.
3. Replace the **API base URL** (and key, if your backend uses one).
4. Click **Save**.
5. Go to **Comments** and click **Fetch comments** — the request now hits your
   custom endpoint instead of Regulations.gov.

The backend is expected to expose a JSON:API-shaped `GET /comments` endpoint
(`data[]` + optional `meta` and `included[]`) and `GET /comments/{id}`. If your
custom backend has a different shape, adjust
[`Services/RegulationsGovModels.cs`](Services/RegulationsGovModels.cs) and
[`Services/RegulationsGovClient.cs`](Services/RegulationsGovClient.cs).
