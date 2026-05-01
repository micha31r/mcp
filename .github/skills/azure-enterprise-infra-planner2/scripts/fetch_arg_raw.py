# /// script
# dependencies = [
#   "azure-identity",
# ]
# ///

import json
import logging
import sys

import requests
from azure.identity import DefaultAzureCredential
from azure.core.exceptions import ClientAuthenticationError

logger = logging.getLogger(__name__)
logger.setLevel(logging.ERROR)

ARG_URL = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01"

KQL_QUERY = """
Resources
| where type !startswith "microsoft.portal/"
| where type !startswith "providers.test/"
| where isempty(managedBy)
| where not (tags contains "hidden-") and not (tags contains "link:")
| where resourceGroup !startswith "mc_"
    and resourceGroup !startswith "databricks-rg-"
    and resourceGroup !startswith "azurebackuprg_"
    and resourceGroup !startswith "defaultresourcegroup-"
    and resourceGroup != "networkwatcherrg"
"""

PAGE_SIZE = 1000


def query_resources() -> dict:
    """Send the ARG query, page through all results, and return the raw combined response."""
    try:
        credential = DefaultAzureCredential()
        token = credential.get_token("https://management.azure.com/.default")
        headers = {
            "Authorization": f"Bearer {token.token}",
            "Content-Type": "application/json",
        }

        combined: dict = {}
        all_rows: list = []
        skip_token: str | None = None
        page = 0

        while True:
            body: dict = {
                "query": KQL_QUERY,
                "options": {"$top": PAGE_SIZE},
            }
            if skip_token:
                body["options"]["$skipToken"] = skip_token

            response = requests.post(ARG_URL, headers=headers, json=body)
            response.raise_for_status()
            payload = response.json()

            page += 1
            rows = payload.get("data", []) or []
            all_rows.extend(rows)
            logger.info(f"Page {page}: fetched {len(rows)} rows (running total {len(all_rows)})")

            combined = payload
            skip_token = payload.get("$skipToken")
            if not skip_token:
                break

        combined["data"] = all_rows
        combined["count"] = len(all_rows)
        combined.pop("$skipToken", None)

        return combined

    except ClientAuthenticationError as e:
        print(f"Error: Not authenticated. No valid Azure credentials found.\n{e}", file=sys.stderr)
        sys.exit(1)

    except Exception as e:
        logger.error(f"Query error: {str(e)}", exc_info=True)
        return {}


if __name__ == "__main__":
    logging.basicConfig(level=logging.ERROR)
    sys.stdout.reconfigure(encoding="utf-8")
    raw = query_resources()
    rows = raw.get("data", []) if isinstance(raw, dict) else []

    print(json.dumps(rows, ensure_ascii=False))
