package main

import (
	"testing"
	"unicode/utf16"
)

func TestParseWingetTable(t *testing.T) {
	en := "   - \r   \\ \r\r\n" +
		"Name                         Id                          Version      Available    Source\r\n" +
		"------------------------------------------------------------------------------------------\r\n" +
		"Mozilla Firefox (x64 en-US)  Mozilla.Firefox             120.0        121.0.1      winget\r\n" +
		"7-Zip 23.01 (x64)            7zip.7zip                   23.01        24.08        winget\r\n" +
		"Some Very Long Program Name… Vendor.SomeVeryLongProgram… 1.0          2.0          winget\r\n" +
		"3 upgrades available.\r\n"
	got := parseWingetTable(en)
	if len(got) != 2 || got[0].ID != "Mozilla.Firefox" || got[0].Version != "120.0" || got[0].Available != "121.0.1" || got[1].ID != "7zip.7zip" || got[1].Source != "winget" {
		t.Fatalf("en: %+v", got)
	}
	ru := "Имя                 ИД                   Версия  Доступно Источник\n" +
		"-----------------------------------------------------------------\n" +
		"Блокнот++ (64-бит)  Notepad++.Notepad++  8.6.0   8.7.1    winget\n" +
		"\n" +
		"Доступно обновлений: 1.\n"
	got = parseWingetTable(ru)
	if len(got) != 1 || got[0].Name != "Блокнот++ (64-бит)" || got[0].ID != "Notepad++.Notepad++" || got[0].Available != "8.7.1" {
		t.Fatalf("ru: %+v", got)
	}
	if len(parseWingetTable("No installed package found matching input criteria.")) != 0 {
		t.Fatal("no table")
	}
}

func TestProgressAndDecode(t *testing.T) {
	for s, want := range map[string]int{
		"Verification 45% complete.":                           45,
		"Проверка 7% завершена.":                               7,
		"[=====                      10.0%                 ] ": 10,
		"Progress: 120 of 4000 done; Stage: 80%; Total: 33%":   33,
	} {
		if v, ok := progressOf(s); !ok || v != want {
			t.Errorf("%q: %d %v", s, v, ok)
		}
	}
	if _, ok := progressOf("no numbers here"); ok {
		t.Error("false progress")
	}
	// sfc writes UTF-16 when redirected; lines are cut at the '\n' byte
	var b []byte
	b = append(b, 0) // high byte of the previous line feed
	for _, u := range utf16.Encode([]rune("Проверка 52% завершена.")) {
		b = append(b, byte(u), byte(u>>8))
	}
	if s := decodeLine(b, func(x []byte) string { return string(x) }); s != "Проверка 52% завершена." {
		t.Fatalf("utf16: %q", s)
	}
	if s := decodeLine([]byte("plain text"), func(x []byte) string { return string(x) }); s != "plain text" {
		t.Fatalf("plain: %q", s)
	}
}

func TestMarkOldDrivers(t *testing.T) {
	pk := []DriverPkg{
		{Inf: "oem1.inf", Original: "nv_dispi.inf", Provider: "NVIDIA", Class: "Display", Version: "31.0.15.3623", Date: "2023-06-01", Size: 900},
		{Inf: "oem7.inf", Original: "nv_dispi.inf", Provider: "NVIDIA", Class: "Display", Version: "31.0.15.5222", Date: "2024-03-01", Size: 950, InUse: true},
		{Inf: "oem4.inf", Original: "nv_dispi.inf", Provider: "NVIDIA", Class: "Display", Version: "31.0.15.4601", Date: "2023-11-01", Size: 920},
		{Inf: "oem2.inf", Original: "nv_dispi.inf", Provider: "NVIDIA", Class: "Display", Version: "30.0.14.9649", Date: "2022-01-01", Size: 800},
		{Inf: "oem9.inf", Original: "rt640x64.inf", Provider: "Realtek", Class: "Net", Version: "10.50", Date: "2021-01-01", Size: 5},
		{Inf: "oem3.inf", Original: "ibtusb.inf", Provider: "Intel", Class: "Bluetooth", Version: "22.0", Date: "2022-01-01", Size: 7},
		{Inf: "oem5.inf", Original: "ibtusb.inf", Provider: "Intel", Class: "Bluetooth", Version: "23.10", Date: "2023-08-01", Size: 7},
	}
	old := map[string]bool{}
	for _, p := range markOldDrivers(pk) {
		if p.Old {
			old[p.Inf] = true
		}
	}
	// NVIDIA: in use 5222 kept, newest other (4601) kept, two older go;
	// Realtek: the only version stays; Intel without a device: newest stays
	if len(old) != 3 || !old["oem1.inf"] || !old["oem2.inf"] || !old["oem3.inf"] {
		t.Fatalf("old: %v", old)
	}
	if compareVersions("10.0.2", "10.0.10") >= 0 || compareVersions("2.0", "2") != 0 {
		t.Fatal("versions")
	}
}

func TestAppxName(t *testing.T) {
	for in, want := range map[string]string{
		"SpotifyAB.SpotifyMusic_zpdnekdrzrea0":   "Spotify Music",
		"5319275A.WhatsAppDesktop_cv1g1gvanyjgm": "Whats App Desktop",
		"Telegram_abc":                           "Telegram",
	} {
		if got := appxName(in); got != want {
			t.Errorf("%s: %q", in, got)
		}
	}
}
