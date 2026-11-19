// rulegen converts Winapp2.ini into Prostor's rules file.
//
//	go run ./cmd/rulegen -in Winapp2.ini -out cmd/prostor/rules/winapp2.json [-rejected rejected.txt]
//
// Only folders that pass rules.CheckLoc are kept; see internal/rules/winapp2.go.
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"sort"

	"prostor/internal/rules"
)

func main() {
	in := flag.String("in", "Winapp2.ini", "Winapp2.ini")
	out := flag.String("out", "cmd/prostor/rules/winapp2.json", "rules file to write")
	rejOut := flag.String("rejected", "", "write the list of rejected entries and keys here")
	flag.Parse()

	r, err := os.Open(*in)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer r.Close()
	f, rej, err := rules.ConvertWinapp2(r)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	// one rule per line: compact, yet diffs stay readable
	var buf bytes.Buffer
	rs := f.Rules
	f.Rules = nil
	head, _ := json.Marshal(f)
	buf.Write(head[:len(head)-len(`,"rules":null}`)])
	buf.WriteString(",\n\"rules\": [\n")
	for i, ru := range rs {
		b, _ := json.Marshal(ru)
		buf.Write(b)
		if i < len(rs)-1 {
			buf.WriteByte(',')
		}
		buf.WriteByte('\n')
	}
	buf.WriteString("]}\n")
	f.Rules = rs
	if _, err := rules.Decode(bytes.NewReader(buf.Bytes())); err != nil {
		fmt.Fprintln(os.Stderr, "проверка файла:", err)
		os.Exit(1)
	}
	if err := os.WriteFile(*out, buf.Bytes(), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	locs := 0
	cats := map[string]int{}
	for _, ru := range f.Rules {
		locs += len(ru.Locs)
		cats[ru.Cat]++
	}
	reasons := map[string]int{}
	for _, x := range rej {
		reasons[x.Reason]++
	}
	fmt.Printf("Winapp2 %s: правил %d, папок %d, отклонено записей и ключей %d\n", f.Version, len(f.Rules), locs, len(rej))
	fmt.Printf("по разделам: %v\n", cats)
	if *rejOut != "" {
		w, _ := os.Create(*rejOut)
		defer w.Close()
		type kv struct {
			k string
			n int
		}
		var top []kv
		for k, n := range reasons {
			top = append(top, kv{k, n})
		}
		sort.Slice(top, func(a, b int) bool { return top[a].n > top[b].n })
		for _, x := range top {
			fmt.Fprintf(w, "%6d  %s\n", x.n, x.k)
		}
		fmt.Fprintln(w)
		for _, x := range rej {
			fmt.Fprintf(w, "%s | %s | %s\n", x.Entry, x.Key, x.Reason)
		}
	}
}
