package main

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	// "os/signal"  // <-- Tidak perlu di Windows
	// "syscall"    // <-- Tidak perlu di Windows

	"github.com/creack/pty" // Library PTY
	"golang.org/x/term"    // Library TTY Raw Mode
)

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "Usage: pty-helper <command> [args...]")
		os.Exit(1)
	}
	
	command := os.Args[1]
	args := os.Args[2:]

	// Buat perintah exec
	cmd := exec.Command(command, args...)

	// Mulai perintah di dalam PTY
	// Di Windows, ini akan otomatis inherit TTY size
	ptmx, err := pty.Start(cmd)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error starting PTY: %v\n", err)
		os.Exit(1)
	}
	defer ptmx.Close()

	// Set STDIN ke RAW mode
	oldState, err := term.MakeRaw(int(os.Stdin.Fd()))
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error setting STDIN to raw mode: %v\n", err)
		os.Exit(1)
	}
	// Kembalikan TTY state saat program exit
	defer term.Restore(int(os.Stdin.Fd()), oldState)

	// Sambungkan I/O
	// Salin output PTY (bot) ke STDOUT (C#)
	go io.Copy(os.Stdout, ptmx)
	// Salin input STDIN (user) ke PTY (bot)
	go io.Copy(ptmx, os.Stdin)

	// Tunggu proses selesai
	if err := cmd.Wait(); err != nil {
		// Keluar dengan exit code dari child process
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		os.Exit(1)
	}
	os.Exit(0)
}
