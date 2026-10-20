# Notices de componentes de terceiros

## protonvpn-wg-confgen

O GVPN inclui o executável `proton-confgen.exe`, compilado a partir do componente
`tools/proton-confgen` do arquivo `GoLiveBypass-2.0.13.zip` fornecido pelo usuário.
O projeto de origem identificado pelo próprio módulo é
https://github.com/hatemosphere/protonvpn-wg-confgen.
O componente é identificado como GPL-3.0. A licença completa e o código-fonte
correspondente estão em `ThirdParty/proton-confgen` e
`ThirdParty/proton-confgen-source.zip` no projeto. A publicação single-file do
Windows incorpora o arquivo de fontes e este aviso dentro de `GVPN.exe`.

Este helper fornece autenticação Proton, consulta de plano e servidor, geração de
perfil WireGuard e persistência de sessão protegida pelo Windows DPAPI. A própria
documentação do componente informa que sua integração com a API Proton foi
reversamente analisada e não é uma API pública oficial para terceiros. A Proton pode
alterar ou bloquear os endpoints, versões e desafios de verificação usados pelo
helper.

O componente mantém seus avisos de copyright e GPL-3.0. Consulte `LICENSE` dentro
do arquivo de fontes correspondente para os termos completos.
