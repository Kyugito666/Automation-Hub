package main

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"os/signal"
	"syscall"

	"github.com/creack/pty" // Ini adalah library kuncinya
	"golang.org/x/term"    // Untuk TTY raw mode
)

func main() {
	// 1. Ambil perintah yang ingin dijalankan (misal: "python", "main.py")
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "Usage: pty-helper <command> [args...]")
		os.Exit(1)
	}
	command := os.Args[1]
	args := os.Args[2:]

	// 2. Buat perintah exec
	cmd := exec.Command(command, args...)

	// 3. Mulai perintah di dalam PTY
	ptmx, err := pty.Start(cmd)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error starting PTY: %v\n", err)
		os.Exit(1)
	}
	// Pastikan PTY ditutup saat selesai
	defer ptmx.Close()

	// 4. Set STDIN (keyboard kita) ke RAW mode
	// Ini kuncinya agar 'y'/'n' bisa ditangkap
	oldState, err := term.MakeRaw(int(os.Stdin.Fd()))
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error setting STDIN to raw mode: %v\n", err)
		os.Exit(1)
	}
	// Kembalikan TTY state saat program exit
	defer term.Restore(int(os.Stdin.Fd()), oldState)

	// 5. Handle window resize (penting untuk TUI)
	ch := make(chan os.Signal, 1)
	signal.Notify(ch, syscall.SIGWINCH)
	go func() {
		for range ch {
			pty.InheritSize(os.Stdin, ptmx)
		}
	}()
	ch <- syscall.SIGWINCH // Trigger sekali saat start

	// 6. Sambungkan I/O
	// Salin output PTY (bot) ke STDOUT (C#)
	go io.Copy(os.Stdout, ptmx)
	// Salin input STDIN (user) ke PTY (bot)
	go io.Copy(ptmx, os.Stdin)

	// 7. Tunggu proses selesai
	if err := cmd.Wait(); err != nil {
		// Keluar dengan exit code dari child process
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		os.Exit(1)
	}
	os.Exit(0)
}
