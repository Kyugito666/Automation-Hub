#!/usr/bin/env node

const pty = require('node-pty');
const os = require('os');

// Ambil command dan args dari C#
// Contoh: node pty-helper.js python bot.py arg1
const args = process.argv.slice(2);
if (args.length === 0) {
    console.error('Usage: pty-helper <command> [args...]');
    process.exit(1);
}
const command = args[0];
const commandArgs = args.slice(1);

// Tentukan shell (penting untuk PATH & environment)
const shell = os.platform() === 'win32' ? 'powershell.exe' : 'bash';

// Spawn proses di PTY
const ptyProcess = pty.spawn(shell, [], {
    name: 'xterm-color',
    cols: process.stdout.columns || 80,
    rows: process.stdout.rows || 30,
    cwd: process.cwd(), // Inherit working directory dari C#
    env: process.env    // Inherit environment variables
});

// Tangani Ctrl+C dari user (kirim ke PTY)
process.stdin.on('data', (key) => {
    ptyProcess.write(key.toString());
});
process.stdin.setRawMode(true); // Langsung kirim keystroke tanpa nunggu Enter
process.stdin.resume();

// Salin output PTY ke STDOUT (yang dibaca C#)
ptyProcess.onData((data) => {
    process.stdout.write(data);
});

// Handle PTY exit
ptyProcess.onExit(({ exitCode, signal }) => {
    process.stdin.setRawMode(false); // Kembalikan stdin ke mode normal
    process.stdin.pause();
    // Keluar dengan exit code yang sama dari PTY
    process.exit(exitCode);
});

// Handle resize (opsional, tapi bagus)
process.stdout.on('resize', () => {
    ptyProcess.resize(process.stdout.columns, process.stdout.rows);
});

// Kirim command asli ke shell PTY
ptyProcess.write(`${command} ${commandArgs.map(arg => `"${arg}"`).join(' ')}\r`); // Kirim command + Enter

// Kirim 'exit' setelah command selesai (biar shell PTY-nya mati)
// Ini penting agar ptyProcess.onExit bisa kepanggil
// Kita beri sedikit delay untuk memastikan command utama sempat jalan
setTimeout(() => {
    ptyProcess.write('exit\r');
}, 500); // Delay 0.5 detik (mungkin perlu disesuaikan)
