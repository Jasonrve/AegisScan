package database

import (
	"context"
	"database/sql"
	"github.com/aegisscan/aegisscan/internal/domain"
	_ "github.com/jackc/pgx/v5/stdlib"
	"os"
	"testing"
)

func TestPostgresCheckpointRoundTrip(t *testing.T) {
	connection := os.Getenv("AEGIS_TEST_DATABASE_URL")
	if connection == "" {
		t.Skip("set AEGIS_TEST_DATABASE_URL to run PostgreSQL integration tests")
	}
	db, err := sql.Open("pgx", connection)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	ctx := context.Background()
	migration, err := os.ReadFile("../../migrations/001_initial.sql")
	if err != nil {
		t.Fatal(err)
	}
	if err := ApplyMigration(ctx, db, string(migration)); err != nil {
		t.Fatal(err)
	}
	store := Postgres{DB: db}
	expected := domain.ScanCheckpoint{RepositoryID: "integration-repo", Scanner: domain.ScannerTruffleHog, CommitSHA: "integration-sha"}
	if err := store.SaveCheckpoint(ctx, expected); err != nil {
		t.Fatal(err)
	}
	got, err := store.GetCheckpoint(ctx, expected.RepositoryID, expected.Scanner)
	if err != nil {
		t.Fatal(err)
	}
	if got != expected {
		t.Fatalf("checkpoint = %#v, expected %#v", got, expected)
	}
}
