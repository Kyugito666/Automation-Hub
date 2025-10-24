import json
from pathlib import Path
from typing import List, Dict, Optional, Tuple, Any
from pydantic import BaseModel, Field
from datetime import datetime
import httpx
from rich.console import Console
from rich.table import Table

console = Console()

# Path
TOKENS_PATH = Path("../config/github_tokens.txt")
STATE_PATH = Path("../.token-state.json")
PROXY_LIST_PATH = Path("../proxysync/proxy.txt")
TOKEN_CACHE_PATH = Path("../.token-cache.json")


class BotExecutionState(BaseModel):
    last_step: int = Field(default=0, alias="last_step")
    captured_inputs: Dict[str, str] = Field(default_factory=dict, alias="captured_inputs")
    last_executed: datetime = Field(default_factory=datetime.utcnow, alias="last_executed")
    token_index: int = Field(default=0, alias="token_index")

    class Config:
        populate_by_name = True


class TokenState(BaseModel):
    current_index: int = Field(default=0, alias="current_index")
    history: Dict[str, BotExecutionState] = Field(default_factory=dict)

    class Config:
        populate_by_name = True


class TokenEntry:
    def __init__(self, token: str):
        self.token: str = token
        self.proxy: Optional[str] = None
        self.username: Optional[str] = None


class _TokenManager:
    """Singleton-like class untuk mengelola state."""

    def __init__(self):
        self._tokens: List[TokenEntry] = []
        self._proxy_list: List[str] = []
        self._state: TokenState = TokenState()
        self._owner: str = ""
        self._repo: str = ""
        self._token_cache: Dict[str, str] = {}
        self.http_timeout = httpx.Timeout(15.0, connect=5.0)

    def initialize(self):
        self._load_tokens()
        self._load_proxy_list()
        self._load_state()
        self._load_token_cache()
        self._assign_proxies_and_usernames()

    def reload_all_configs(self):
        console.print("[bold yellow]Reloading semua file konfigurasi...[/]")
        self._tokens.clear()
        self._proxy_list.clear()
        self._token_cache.clear()
        self._owner = ""
        self._repo = ""
        self._state = TokenState()  # Reset state
        self.initialize()
        console.print("[bold green]✓ Konfigurasi berhasil di-refresh.[/]")

    def _load_token_cache(self):
        if TOKEN_CACHE_PATH.exists():
            try:
                with open(TOKEN_CACHE_PATH, "r") as f:
                    self._token_cache = json.load(f)
                console.print(f"[green]Loaded {len(self._token_cache)} username dari cache[/]")
            except Exception as e:
                console.print(f"[yellow]Warning: Gagal memuat token cache: {e}[/]")
                self._token_cache = {}

    def save_token_cache(self, cache: Dict[str, str]):
        try:
            self._token_cache = cache
            with open(TOKEN_CACHE_PATH, "w") as f:
                json.dump(self._token_cache, f, indent=2)
        except Exception as e:
            console.print(f"[red]Error saving token cache: {e}[/]")

    def _load_tokens(self):
        if not TOKENS_PATH.exists():
            console.print(f"[red]ERROR: {TOKENS_PATH} tidak ditemukan![/]")
            console.print("[yellow]Buat file dengan format:[/]")
            console.print("[dim]Line 1: owner\nLine 2: repo\nLine 3: token1,token2,token3[/]")
            return

        try:
            with open(TOKENS_PATH, "r") as f:
                lines = f.readlines()
            
            if len(lines) < 3:
                console.print("[red]ERROR: github_tokens.txt format salah![/]")
                return

            self._owner = lines[0].strip()
            self._repo = lines[1].strip()
            
            tokens_str = lines[2].strip()
            self._tokens = [TokenEntry(t.strip()) for t in tokens_str.split(",") if t.strip()]
            
            console.print(f"[green]Loaded {len(self._tokens)} tokens untuk {self._owner}/{self._repo}[/]")

        except Exception as e:
            console.print(f"[red]Gagal membaca {TOKENS_PATH}: {e}[/]")

    def _load_proxy_list(self):
        if PROXY_LIST_PATH.exists():
            try:
                with open(PROXY_LIST_PATH, "r") as f:
                    self._proxy_list = [line.strip() for line in f if line.strip()]
                console.print(f"[green]Loaded {len(self._proxy_list)} proxy dari ProxySync[/]")
            except Exception as e:
                console.print(f"[yellow]Gagal membaca {PROXY_LIST_PATH}: {e}[/]")
        else:
            console.print("[yellow]ProxySync proxy.txt tidak ditemukan[/]")

    def _assign_proxies_and_usernames(self):
        if not self._tokens:
            return
        
        for i, entry in enumerate(self._tokens):
            if self._proxy_list:
                entry.proxy = self._proxy_list[i % len(self._proxy_list)]
            
            if entry.token in self._token_cache:
                entry.username = self._token_cache[entry.token]

    def _load_state(self):
        if STATE_PATH.exists():
            try:
                self._state = TokenState.model_validate_json(STATE_PATH.read_text())
            except Exception as e:
                console.print(f"[yellow]Warning: Gagal memuat state ({e}), membuat state baru.[/]")
                self._state = TokenState()
        else:
            self._state = TokenState()

    def _save_state(self):
        try:
            with open(STATE_PATH, "w") as f:
                f.write(self._state.model_dump_json(indent=2, by_alias=True))
        except Exception as e:
            console.print(f"[red]Error saving state: {e}[/]")

    def get_current_token_entry(self) -> Optional[TokenEntry]:
        if not self._tokens:
            return None
        return self._tokens[self._state.current_index]

    def get_repo_info(self) -> Tuple[str, str]:
        return self._owner, self._repo

    def get_all_token_entries(self) -> List[TokenEntry]:
        return self._tokens

    def get_username_cache(self) -> Dict[str, str]:
        return self._token_cache

    def switch_to_next_token(self):
        if not self._tokens:
            return
        
        self._state.current_index = (self._state.current_index + 1) % len(self._tokens)
        self._save_state()
        
        current = self.get_current_token_entry()
        if current:
            console.print(f"[yellow]Switch ke token #{self._state.current_index + 1} ({current.username or '???'})[/]")
            if current.proxy:
                console.print(f"[dim]Proxy: {current.proxy}[/]")

    def create_http_client(self, entry: Optional[TokenEntry] = None) -> httpx.AsyncClient:
        if entry is None:
            entry = self.get_current_token_entry()
            
        if entry is None:
            raise Exception("Tidak ada token yang dikonfigurasi")
            
        headers = {
            "Authorization": f"Bearer {entry.token}",
            "User-Agent": "Automation-Hub-Orchestrator/2.0-Python",
            "Accept": "application/vnd.github+json"
        }
        
        proxies = None
        if entry.proxy:
            # Asumsi format proxy: http://user:pass@host:port atau http://host:port
            if not entry.proxy.startswith(("http://", "https://", "socks5://")):
                proxy_url = f"http://{entry.proxy}"
            else:
                proxy_url = entry.proxy
            
            proxies = {
                "http://": proxy_url,
                "https://": proxy_url
            }
        
        return httpx.AsyncClient(headers=headers, proxies=proxies, timeout=self.http_timeout)

    def mask_token(self, token: str) -> str:
        return token[:10] + "..." + token[-7:] if len(token) > 20 else token

    def show_status(self):
        if not self._tokens:
            return
            
        table = Table(title="Status GitHub Token", expand=True)
        table.add_column("Index", style="dim")
        table.add_column("Token")
        table.add_column("Username")
        table.add_column("Proxy")
        table.add_column("Aktif", justify="center")

        for i, token in enumerate(self._tokens):
            is_active = "[green]✓[/]" if i == self._state.current_index else ""
            token_display = self.mask_token(token.token)
            
            proxy_display = token.proxy or "[yellow]no proxy[/]"
            if token.proxy and len(token.proxy) > 40:
                proxy_display = token.proxy[:37] + "..."

            table.add_row(
                str(i + 1),
                token_display,
                token.username or "[grey]???[/]",
                proxy_display,
                is_active
            )
        
        console.print(table)
        if self._proxy_list:
            console.print(f"\n[dim]Total proxy dari ProxySync: {len(self._proxy_list)}[/]")
        
        if self._state.history:
            console.print("\n[cyan]Histori Eksekusi:[/]")
            history_table = Table(expand=True)
            history_table.add_column("Bot")
            history_table.add_column("Last Step")
            history_table.add_column("Token Used")
            history_table.add_column("Last Run")
            
            sorted_history = sorted(
                self._state.history.items(), 
                key=lambda item: item[1].last_executed, 
                reverse=True
            )
            
            for bot_name, state in sorted_history:
                history_table.add_row(
                    bot_name,
                    str(state.last_step),
                    f"#{state.token_index + 1}",
                    state.last_executed.strftime("%Y-%m-%d %H:%M")
                )
            console.print(history_table)

# Inisialisasi instance singleton
TokenManager = _TokenManager()
