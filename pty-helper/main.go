package main

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"runtime"

	"github.com/creack/pty"
	"golang.org/x/term"
)

func main() {
	// === FIX: Deteksi Windows & Kasih Error Message Jelas ===
	if runtime.GOOS == "windows" {
		fmt.Fprintln(os.Stderr, "ERROR: PTY not supported on Windows.")
		fmt.Fprintln(os.Stderr, "Use Direct Execution Mode instead (handled automatically by C# Orchestrator).")
		os.Exit(1)
	}

	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "Usage: pty-helper <command> [args...]")
		os.Exit(1)
	}
	
	command := os.Args[1]
	args := os.Args[2:]

	cmd := exec.Command(command, args...)

	ptmx, err := pty.Start(cmd)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error starting PTY: %v\n", err)
		fmt.Fprintln(os.Stderr, "This helper only works on Linux/macOS.")
		os.Exit(1)
	}
	defer ptmx.Close()

	oldState, err := term.MakeRaw(int(os.Stdin.Fd()))
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error setting STDIN to raw mode: %v\n", err)
		os.Exit(1)
	}
	defer term.Restore(int(os.Stdin.Fd()), oldState)

	go io.Copy(os.Stdout, ptmx)
	go io.Copy(ptmx, os.Stdin)

	if err := cmd.Wait(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			os.Exit(exitErr.ExitCode())
		}
		os.Exit(1)
	}
	os.Exit(0)
}
