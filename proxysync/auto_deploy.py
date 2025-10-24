#!/usr/bin/env python3
"""
ProxySync Auto Deploy - Non-Interactive Mode
Taruh file ini di folder proxysync/
"""

import sys
import os

# Import fungsi dari main.py ProxySync
# Sesuaikan nama fungsi dengan yang ada di main.py lu
try:
    from main import (
        download_proxies,      # Fungsi download dari API
        convert_proxylist,     # Fungsi convert format
        test_proxies,          # Fungsi test proxy
        distribute_proxies     # Fungsi distribute ke paths
    )
except ImportError as e:
    print(f"ERROR: Tidak bisa import fungsi dari main.py")
    print(f"Detail: {e}")
    print("\nPastikan file ini ada di folder yang sama dengan main.py")
    sys.exit(1)

def auto_deploy():
    """Jalankan semua step ProxySync otomatis"""
    print("=" * 60)
    print("ProxySync Auto Deploy Mode")
    print("=" * 60)
    
    try:
        # Step 1: Download proxies dari API list
        print("\n[1/4] Downloading proxies dari API list...")
        download_proxies()
        print("✓ Download selesai")
        
        # Step 2: Convert format proxy
        print("\n[2/4] Converting proxy format...")
        convert_proxylist()
        print("✓ Convert selesai")
        
        # Step 3: Test proxy accuracy
        print("\n[3/4] Testing proxy accuracy...")
        test_proxies()
        print("✓ Test selesai")
        
        # Step 4: Distribute ke folder bot
        print("\n[4/4] Distributing proxies ke paths...")
        distribute_proxies()
        print("✓ Distribute selesai")
        
        print("\n" + "=" * 60)
        print("✅ AUTO DEPLOY COMPLETED SUCCESSFULLY")
        print("=" * 60)
        
    except Exception as e:
        print(f"\n❌ ERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)

if __name__ == "__main__":
    auto_deploy()
