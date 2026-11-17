"""Default locations inside the ShipyardTwin project."""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
MODELS = DATA / "models"
TWIN = DATA / "twin"
UNITY_RESOURCES = ROOT / "unity" / "Assets" / "ShipyardTwin" / "Runtime" / "Resources" / "ShipyardTwin"
