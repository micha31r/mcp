# /// script
# dependencies = [
#   "azure-identity",
# ]
# ///

import json
import logging
import sys
from collections import Counter

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
| project id, name, type, kind, location, resourceGroup, subscriptionId, sku, identity, properties
"""

PAGE_SIZE = 1000


def query_resources() -> dict:
    """Send the ARG query, page through all results, and write the raw combined response."""
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
            # break  # Remove this line to enable full pagination

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


def flatten_and_aggregate_properties(resource: dict, prefix: str = "") -> dict:
    """Recursively flatten nested properties into dot notation."""
    result = {}

    if isinstance(resource, dict):
        for key, value in resource.items():
            new_key = f"{prefix}.{key}" if prefix else key

            if isinstance(value, dict):
                result.update(flatten_and_aggregate_properties(value, new_key))
            elif isinstance(value, list):
                result[new_key] = f"<list[{len(value)}]>"
            elif value is not None:
                result[new_key] = str(value)

    return result


def aggregate_resources(rows: list) -> dict:
    """Group ARG rows by type and aggregate property value frequencies."""
    resource_data: dict = {}

    for row in rows:
        resource_type = (row.get("type") or "").lower()
        if not resource_type:
            continue

        if resource_type not in resource_data:
            resource_data[resource_type] = {
                "count": 0,
                "property_aggregations": {},
            }

        resource_data[resource_type]["count"] += 1

        flattened = flatten_and_aggregate_properties({
            "location": row.get("location"),
            "sku": row.get("sku", {}) or {},
            "properties": row.get("properties", {}) or {},
        })

        aggregations = resource_data[resource_type]["property_aggregations"]
        for prop_key, prop_value in flattened.items():
            if prop_key not in aggregations:
                aggregations[prop_key] = Counter()
            aggregations[prop_key][prop_value] += 1

    result_map: dict = {}
    for resource_type, res_data in resource_data.items():
        result_map[resource_type] = {
            "armResourceType": resource_type,
            "totalCount": res_data["count"],
            "propertyAggregations": {
                key: dict(counter.most_common(3))
                for key, counter in res_data["property_aggregations"].items()
            },
        }

    logger.info(f"Aggregated data for {len(result_map)} resource types")
    return result_map


if __name__ == "__main__":
    logging.basicConfig(level=logging.ERROR)
    raw = query_resources()
    rows = raw.get("data", []) if isinstance(raw, dict) else []

    aggregated = aggregate_resources(rows)
    print(json.dumps(aggregated, indent=2))
