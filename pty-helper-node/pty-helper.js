#!/usr/bin/env node

const pty = require('node-pty');
const os = require('os');

// Argumen:
// 1. (Opsional) file input: Path ke file .txt yang berisi jawaban (untuk mode auto)
// 2. Command: Perintah yang akan dieksekusi (e.g., "python")
// 3. ...Args: Argumen untuk perintah (e.g., "run.py", "--arg1")
//
// Contoh Manual: pty-helper python run.py
// Contoh Auto:   pty-helper ../config/bot-answers/my_bot.txt python run.py

const args = process.argv.slice(2);
if (args.length === 0) {
    console.error('Usage (manual):   pty-helper <command> [args...]');
    console.error('Usage (scripted): pty-helper <input_file.txt> <command> [args...]');
    process.exit(1);
}

let inputFile = null;
let command;
let commandArgs;

// Cek apakah argumen pertama adalah file input
const fs = require('fs');
if (args.length > 1 && (args[0].endsWith('.txt') || args[0].endsWith('.json'))) {
    try {
        if (fs.existsSync(args[0])) {
            inputFile = args[0];
            command = args[1];
            commandArgs = args.slice(2);
        }
    } catch (e) {
        // Abaikan, anggap itu bukan file
    }
}

if (inputFile === null) {
    // Mode Manual
    command = args[0];
    commandArgs = args.slice(1);
}

const shell = os.platform() === 'win32' ? 'powershell.exe' : 'bash';
const ptyProcess = pty.spawn(shell, [], {
    name: 'xterm-color',
    cols: process.stdout.columns || 80,
    rows: process.stdout.rows || 30,
    cwd: process.cwd(), // Inherit working directory
    env: process.env    // Inherit environment
});

// Salin output PTY ke STDOUT
let lastOutput = '';
ptyProcess.onData((data) => {
    process.stdout.write(data);
    lastOutput = data; // Simpan output terakhir
});

// Handle PTY exit
ptyProcess.onExit(({ exitCode, signal }) => {
    // Pastikan TTY dikembalikan ke mode normal
    try {
        process.stdin.setRawMode(false);
        process.stdin.pause();
    } catch(e) {}
    process.exit(exitCode);
});

// Handle resize (opsional, tapi bagus)
process.stdout.on('resize', () => {
    try {
        ptyProcess.resize(process.stdout.columns, process.stdout.rows);
    } catch(e) {}
});

// Kirim command asli ke shell PTY
const fullCommand = `${command} ${commandArgs.map(arg => `${arg}`).join(' ')}\r`;
ptyProcess.write(fullCommand);

if (inputFile) {
    // === MODE AUTO (SCRIPTED) ===
    console.log(`[PTY-HELPER] Running in SCRIPTED mode with input: ${inputFile}`);
    try {
        let inputData;
        
        if (inputFile.endsWith('.json')) {
            // Jika .json, ambil values-nya dan gabung dengan newline
            const json = JSON.parse(fs.readFileSync(inputFile, 'utf-8'));
            inputData = Object.values(json).join('\n');
        } else {
            // Jika .txt, baca apa adanya
            inputData = fs.readFileSync(inputFile, 'utf-8');
        }

        // Pastikan diakhiri newline
        if (!inputData.endsWith('\n')) {
            inputData += '\n';
        }
        
        // Kirim input ke PTY
        ptyProcess.write(inputData);
        
        // Kirim 'exit' setelah command selesai untuk menutup PTY
        // Kita tidak bisa tahu kapan command selesai, jadi kita kirim 'exit'
        // dan biarkan PTY menangani sisanya.
        ptyProcess.write('exit\r');
        
    } catch (e) {
        console.error(`[PTY-HELPER] Error reading input file: ${e.message}`);
        ptyProcess.write('exit\r');
        process.exit(1);
    }
    
} else {
    // === MODE MANUAL (INTERACTIVE) ===
    // console.log('[PTY-HELPER] Running in INTERACTIVE mode');
    try {
        process.stdin.setRawMode(true);
        process.stdin.resume();
    } catch(e) {
        console.error(`[PTY-HELPER] Failed to set raw mode. Interactive input may fail. ${e.message}`);
    }

    // Tangani Ctrl+C/Input dari user (kirim ke PTY)
    process.stdin.on('data', (key) => {
        ptyProcess.write(key);
    });
    
    // Kirim 'exit' setelah command selesai (biar shell PTY-nya mati)
    // Ini penting agar ptyProcess.onExit bisa kepanggil
    ptyProcess.write('exit\r');
}
