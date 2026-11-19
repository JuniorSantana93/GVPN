# GVPN

Cliente de VPN para Windows com interface WebView2 e integração com uma conta Proton VPN.

> **Projeto comunitário e independente.** GVPN não é afiliado, endossado nem mantido pela Proton AG. A autenticação e a obtenção de configurações usam o componente de terceiros `protonvpn-wg-confgen`, cuja integração com a Proton é baseada em engenharia reversa e pode deixar de funcionar se os serviços da Proton mudarem. Consulte [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) e a licença GPL-3.0 do componente.

## Recursos

- Login Proton e lista de servidores conforme o plano confirmado pela conta.
- Conexão WireGuard via WireSock, com validação de conectividade do túnel e monitoramento de falhas.
- Split tunneling por aplicativo.
- IPv6 mantido ativo no Windows.
- Interface responsiva em português, construída em HTML e WebView2.
- Perfil WebView2 armazenado em `%LOCALAPPDATA%\GVPN\WebView2`.
- Verificação de novas versões pelo GitHub ao iniciar, com consulta manual em Configurações.

## Requisitos

- Windows 10 ou 11 de 64 bits.
- Microsoft Edge WebView2 Runtime Evergreen instalado.
- Uma conta Proton VPN válida.
- Privilégios administrativos do Windows para criar e gerenciar o túnel VPN.
- O GVPN oferece instalar o WireSock SDK oficial pelo WinGet quando ele não está presente. É necessária conexão à internet, WinGet disponível e confirmação dos termos/licença do fornecedor. A licença gratuita do SDK é limitada a uso não comercial.
- Se a instalação do WireSock for recusada ou não estiver disponível, Split Tunneling exige instalação manual do WireSock SDK. Para conexão completa sem WireSock, o cliente oficial do WireGuard é necessário.

O executável publicado é self-contained para o .NET, mas o Runtime Evergreen do WebView2 é instalado e atualizado separadamente pelo Windows/Edge.

Na primeira conexão sem WireSock, o GVPN pergunta se pode instalar `NTKERNEL.WireSockVPNClientCLI` pelo WinGet. O instalador oficial é executado fora do GVPN e os termos do pacote devem ser aceitos pelo usuário. O SDK e seus drivers não são empacotados nem redistribuídos pelo projeto.

## Obter o aplicativo

Baixe `GVPN.exe` na página [Releases](../../releases/latest). O app é distribuído como um único executável; no primeiro uso, o componente Proton auxiliar é extraído para `%LOCALAPPDATA%\GVPN\bin`.

## Compilar

1. Instale o .NET SDK 8 e o Go compatível com `ThirdParty/proton-confgen/go.mod`.
2. Compile o helper para Windows x64 com a versão oficial atual:

   ```powershell
   Push-Location ThirdParty/proton-confgen
   $versionText = Invoke-RestMethod 'https://raw.githubusercontent.com/ProtonVPN/proton-vpn-gtk-app/stable/versions.yml'
   $match = [regex]::Match($versionText, '(?m)^version:\s*(\d+\.\d+\.\d+)\s*$')
   if (-not $match.Success) { throw 'Não foi possível validar a versão atual da Proton.' }
   $version = $match.Groups[1].Value
   $env:GOOS = 'windows'
   $env:GOARCH = 'amd64'
   $ldflags = "-X 'proton-vpn-wg-confgen/internal/constants.AppVersion=linux-vpn@$version' -X 'proton-vpn-wg-confgen/internal/constants.UserAgent=ProtonVPN/$version (Linux; Ubuntu)'"
   go build -ldflags $ldflags -o build/proton-confgen.exe ./cmd/protonvpn-wg/main.go
   Pop-Location
   ```

3. Publique o app:

   ```powershell
   dotnet publish .\GVPN.csproj -c Release -r win-x64 --self-contained true -o .\publish
   ```

O resultado `publish/GVPN.exe` contém a interface, o helper Proton e as dependências .NET. O runtime Evergreen do WebView2 continua sendo um requisito externo.

## Publicar uma versão

Depois de enviar uma tag de versão, o workflow [`.github/workflows/release.yml`](.github/workflows/release.yml) busca e valida a versão oficial da Proton antes de compilar. Se não conseguir obter uma versão válida, a publicação é interrompida.

O workflow [`.github/workflows/check-proton-version.yml`](.github/workflows/check-proton-version.yml) consulta diariamente a versão oficial. Quando detectar uma versão nova, atualiza o componente, incrementa o patch do GVPN, publica o executável e cria uma release automaticamente. Se a fonte estiver indisponível ou retornar dados inválidos, não altera o projeto nem publica uma versão.

## Aviso de segurança e privacidade

As credenciais são enviadas ao helper local para autenticação e a sessão é protegida pelo Windows DPAPI. O GVPN não opera um serviço próprio de VPN; a conexão é estabelecida com a infraestrutura da conta Proton. Revise o código e os avisos de terceiros antes de usar ou redistribuir.
