package main

import (
	"errors"
	"testing"
	"time"

	"protonvpn-wg-confgen/internal/api"
	"protonvpn-wg-confgen/internal/auth"
)

// A GUI decidia "deslogado" a partir de valid=false, entao uma falha temporaria
// de verificacao (rede/API) bloqueava o botao de ativar com o usuario ainda
// autenticado -- o relato de tres usuarios em 18/09 (#312, #316, #317). O codigo
// precisa distinguir os dois casos.
func TestSessionCheckResponseSeparatesTemporaryFailureFromInvalidSession(t *testing.T) {
	temporary := sessionCheckResponse(nil, &auth.TemporarySessionError{Operation: "check-session"}, "conta_teste", 0)
	if temporary["valid"] != false || temporary["success"] != false {
		t.Fatalf("falha temporaria marcada como valida: %v", temporary)
	}
	if temporary["code"] != "NETWORK_ERROR" {
		t.Fatalf("code = %v, want NETWORK_ERROR", temporary["code"])
	}
	if temporary["retryable"] != true {
		t.Fatalf("retryable = %v, want true", temporary["retryable"])
	}
	if message, _ := temporary["error"].(string); message == "" {
		t.Fatal("falha temporaria sem mensagem")
	}

	invalid := sessionCheckResponse(nil, errors.New("session expired"), "conta_teste", 0)
	if invalid["code"] != "INVALID_SESSION" {
		t.Fatalf("code = %v, want INVALID_SESSION", invalid["code"])
	}
	if invalid["retryable"] != false {
		t.Fatalf("retryable = %v, want false", invalid["retryable"])
	}

	// Sessao ausente sem erro tipado tambem e invalida, nunca temporaria.
	missing := sessionCheckResponse(nil, nil, "conta_teste", 0)
	if missing["code"] != "INVALID_SESSION" || missing["retryable"] != false {
		t.Fatalf("sessao ausente = %v, want INVALID_SESSION retryable=false", missing)
	}
}

func TestSessionCheckResponseKeepsValidSessionContract(t *testing.T) {
	response := sessionCheckResponse(&api.Session{UID: "uid-teste"}, nil, "conta_teste", 90*time.Minute)
	if response["success"] != true || response["valid"] != true {
		t.Fatalf("sessao valida rejeitada: %v", response)
	}
	if response["username"] != "conta_teste" {
		t.Fatalf("username = %v, want conta_teste", response["username"])
	}
	if response["expiresIn"] != "1h30m0s" {
		t.Fatalf("expiresIn = %v, want 1h30m0s", response["expiresIn"])
	}
	// O contrato de sucesso nao carrega segredo: apenas identidade e validade.
	for _, leaked := range []string{"AccessToken", "RefreshToken", "PrivateKey"} {
		if _, ok := response[leaked]; ok {
			t.Fatalf("resposta expoe %s", leaked)
		}
	}
}
