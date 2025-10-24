import json
from pathlib import Path
from typing import List, Optional
from pydantic import BaseModel, Field, field_validator
from rich.console import Console

console = Console()
CONFIG_FILE = Path("../config/bots_config.json")


class BotEntry(BaseModel):
    name: str
    path: str
    repo_url: str = Field(..., alias="repo_url")
    enabled: bool
    type: str  # "python" or "javascript"

    @property
    def is_bot(self) -> bool:
        """Property untuk menentukan apakah ini bot (bukan tool)."""
        return "/privatekey/" in self.path or "/token/" in self.path

    class Config:
        populate_by_name = True


class BotConfig(BaseModel):
    bots_and_tools: List[BotEntry] = Field(..., alias="bots_and_tools")

    @classmethod
    def load(cls) -> Optional["BotConfig"]:
        if not CONFIG_FILE.exists():
            console.print(f"[red]Error: File konfig '{CONFIG_FILE}' tidak ditemukan.[/]")
            return None
        try:
            with open(CONFIG_FILE, "r") as f:
                data = json.load(f)
            return cls.model_validate(data)
        except json.JSONDecodeError as e:
            console.print(f"[red]Error parsing {CONFIG_FILE}: {e}[/]")
            return None
        except Exception as e:
            console.print(f"[red]Error memuat config: {e}[/]")
            return None

# Opsi untuk menu "Back"
BACK_OPTION = BotEntry(
    name="[[Back]] Kembali", path="", repo_url="", enabled=False, type="SYSTEM"
)
