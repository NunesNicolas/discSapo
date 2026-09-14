# discSapo 0.6.1 — Windows

Aplicativo nativo WPF com Discord web em WebView2 e conexão WireGuard em espaço de usuário. Usa sua conta Proton ou um arquivo WireGuard próprio. Não exige o Proton VPN instalado e não altera as rotas de rede do Windows.

Projeto independente, sem afiliação ao Discord ou à Proton. Esta versão não tem instalador, assinatura de código nem atualização automática.

## Compartilhar e executar

Para enviar apenas o aplicativo, compacte **a pasta inteira `artifacts/app-0.6.1`**. Quem receber deve extrair o ZIP antes de abrir `DiscordVpn.exe`. Não envie somente o executável: as DLLs, o runtime .NET, `wiresocks.exe` e `proton-bridge.exe` também são necessários. Preserve as licenças e `ProtonBridge-source`.

Requisitos: **Windows x64** e [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). O runtime .NET já acompanha o pacote. Cada pessoa usa sua própria conta Proton ou configuração WireGuard.

Para compartilhar também o código-fonte, compacte a raiz deste projeto. Após a limpeza, permanecem:

| Local | Conteúdo |
| --- | --- |
| `artifacts/app-0.6.1/` | Aplicativo pronto, dependências e fontes/licença do helper Proton |
| `src/` | Código-fonte WPF e do helper Proton |
| `tests/` | Código dos testes, sem perfis ou resultados gerados |
| `scripts/` | Compilação, geração do logo e limpeza para compartilhamento |
| `discSapoLogo.svg` | Logo original |
| `THIRD-PARTY-wiresocks.txt` | Avisos/licença do motor de conexão |

Não inclua arquivos pessoais `.conf`, logs, dumps, cookies ou cópias da pasta de dados do usuário no ZIP. Após compilar ou executar testes, use `scripts/clean-for-sharing.ps1` para retirar caches, versões antigas e resultados gerados. Ele preserva a versão 0.6.1 e o código-fonte, mas remove `teste.conf` da raiz, se existir.

## Uso

1. No card da conta, clique em **Conectar com Proton**. Informe sua conta e o código do autenticador, se solicitado. Escolha um país ou **Automático** e clique em **Conectar e abrir Discord**. O app filtra servidores pelo plano confirmado pela Proton.
2. A seta do card oferece **Abrir portal Proton** e **Conectar com Proton**. No portal, um download WireGuard válido é importado e conectado automaticamente. **Importar arquivo** permite usar uma configuração existente.
3. Nas próximas aberturas, **Conectar →** renova a sessão salva e gera a configuração automaticamente. Para trocar o país, abra o fluxo Proton novamente.
4. O Discord só abre depois que o túnel responde à verificação e o navegador confirma o mesmo IP de saída. **Desconectar** fecha o navegador e o motor.

O header e a barra de hover acompanham o tema de aplicativos do Windows. **Ocultar controles** remove cabeçalho e rodapé; aproximar o ponteiro do topo revela a barra. Ela espera 900 ms para desaparecer ao sair da região; retornar cancela essa espera. **Revelar controles** ou `Ctrl+Shift+H` restaura o header.

## Onde ficam os dados do usuário

Os dados de uso ficam **fora da pasta do executável**, na conta do Windows que executa o aplicativo:

```text
%LOCALAPPDATA%\DiscordVpn
```

Normalmente, isso corresponde a `C:\Users\<usuario>\AppData\Local\DiscordVpn`. Cole `%LOCALAPPDATA%\DiscordVpn` na barra de endereço do Explorador para abrir a pasta. Copiar somente a pasta de distribuição não copia esses dados.

| Local dentro dessa pasta | Dados e proteção |
| --- | --- |
| `Configuration\account.bin` | Tokens de acesso/renovação Proton, nome do usuário, país, filtro de servidores e seleção do fluxo direto. Criptografados com Windows DPAPI, escopo `CurrentUser`. Não contém a senha Proton. |
| `Configuration\proton.bin` | Configuração WireGuard importada pelo portal, incluindo a chave privada. Criptografada com DPAPI `CurrentUser`. |
| `Configuration\*.conf` | Temporários **em texto claro**, com chaves WireGuard, necessários durante downloads/conexões. Removidos no encerramento normal; podem restar após falhas ou encerramento forçado. |
| `WebView2\` | Perfil do navegador Discord: cookies de sessão, armazenamento de sites, cache e outros dados gerenciados pelo WebView2. Podem manter o usuário conectado. Não são protegidos pelo arquivo DPAPI do app. |
| `ProtonPortal\` | Perfil separado do portal Proton, incluindo cookies e armazenamento da página. Pode manter o login no portal. |
| `browser.log` | Diagnóstico local: horários, etapa/tipo de falha, versão do navegador, status HTTP e nomes de hosts com erro. Pode revelar os serviços acessados. O código do app não registra senhas, tokens, chaves, conteúdo das páginas ou URLs completas nesse log. |

Arquivos importados manualmente permanecem **no local escolhido pelo usuário**, por exemplo Downloads. O app lê esse arquivo ao conectar e não o remove. Uma cópia baixada fora do fluxo automático também pode continuar em Downloads.

As decisões de câmera/microfone ficam em memória até desconectar. Cookies e dados de sites seguem as regras do WebView2; os perfis de navegador devem ser tratados como dados sensíveis.

### Sair, esquecer e apagar dados

- **Desconectar** encerra a conexão, mas preserva sessões, cookies e configurações salvas.
- **Esquecer conta neste PC**, na janela Proton, remove `account.bin`. Não revoga a sessão remotamente, não apaga `proton.bin` e não limpa cookies do portal ou do Discord.
- Para limpar os dados locais do app, feche todas as suas janelas e apague `%LOCALAPPDATA%\DiscordVpn` pelo Explorador. Será necessário configurar a conexão e entrar nos sites novamente. Isso também apaga os diagnósticos locais.
- Essa limpeza não exclui contas ou mensagens nos serviços, nem arquivos `.conf` importados de outras pastas. Para revogar acesso remoto, use os controles de sessões dos respectivos serviços.

Não compartilhe `account.bin`, `proton.bin`, os perfis WebView2 ou um `.conf`, mesmo que algum deles esteja criptografado. Exclusão comum de arquivos não é apagamento seguro e não remove cópias em backups.

## Segurança e limites

### Proteções implementadas

- Login direto Proton com SRP e verificação da prova do servidor. A senha não é persistida pelo app. Senha, TOTP, tokens e configuração passam entre app e helper por pipes redirecionados, sem colocá-los na linha de comando ou em logs de diagnóstico.
- Tokens e configuração importada pelo portal são armazenados com DPAPI `CurrentUser`. Os dados são descriptografados quando necessários; credenciais existem na memória dos processos durante o uso.
- O portal aceita origens de conta Proton HTTPS conhecidas. Downloads são verificados por origem, tamanho e estrutura. O script de captura de downloads não lê formulários de login.
- O navegador usa proxy SOCKS5 local sem alternativa `DIRECT`, com resolução de destinos pelo proxy e QUIC/UDP não intermediado do WebRTC desabilitados.
- O app verifica o IP pelo túnel, compara com a saída do navegador e revalida a conexão a cada 20 segundos. Falhas detectadas fecham a WebView. O motor usa supervisão com encerramento ao fechar o processo supervisor.
- Câmera e microfone exigem confirmação em uma janela que mostra a origem HTTPS solicitante. Outros pedidos de permissão e downloads do navegador Discord são bloqueados pelo app.

### O que essas proteções não garantem

- **A proteção se aplica ao navegador do aplicativo, não ao computador inteiro.** Outros programas continuam usando a rede normal. O login/consulta de perfil Proton e o portal usam a conexão normal para preparar a VPN.
- **DPAPI não protege contra malware executado com seu usuário do Windows nem contra um computador comprometido.** Processos com acesso equivalente podem ler memória, descriptografar dados no mesmo contexto ou acessar temporários. Não há senha mestra adicional.
- O proxy escuta somente em `127.0.0.1`, em porta aleatória, mas não autentica outros processos locais. Não é uma fronteira de segurança contra outros programas do computador.
- A verificação periódica não é um kill switch de firewall. Não há auditoria independente nem comprovação completa de ausência de vazamentos DNS, IPv4, IPv6 ou WebRTC. Políticas e parâmetros externos do WebView2 podem interferir no comportamento.
- VPN não torna uma conta anônima: Proton, Discord e páginas acessadas continuam recebendo as informações necessárias aos serviços. A verificação de saída consulta `www.cloudflare.com/cdn-cgi/trace` pelo túnel.
- Chamadas de voz/vídeo e compartilhamento de tela **não foram validados**. O SOCKS5 do Chromium transporta TCP; a restrição de UDP pode impedir chamadas.
- A integração Proton é não oficial e depende de interfaces internas. Mudanças na API, CAPTCHA, chaves de segurança e contas em modo legado podem exigir o portal ou atualização do app. O aplicativo não contorna essas verificações.
- Não há atualização automática. Mantenha Windows e WebView2 atualizados e substitua o pacote por versões confiáveis quando disponíveis. Não execute cópias de origem desconhecida.

O helper solicita certificados de sessão de até sete dias. Uma nova configuração é gerada a cada conexão. Não há renovação do certificado durante uma conexão contínua: perto do vencimento, é necessário reconectar. Sessões expiradas/revogadas exigem novo login.

## Compilar a partir do código

Requisitos de desenvolvimento: Windows x64, SDK .NET 8 compatível e internet para restaurar dependências. Após a limpeza, caches e compiladores locais serão baixados novamente pelo script.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -OutputDirectory artifacts/app-0.6.1
.\artifacts\app-0.6.1\DiscordVpn.exe
```

Feche o app antes de recompilar para a mesma pasta. O script baixa o bootstrap Go com SHA-256 fixo, compila `wiresocks` de um commit fixo, usa Go 1.26.6 para o helper Proton e publica o runtime .NET junto ao aplicativo. `.tools`, `bin`, `obj` e `artifacts/tunnel` são recriadas no desenvolvimento e não precisam acompanhar o código compartilhado.

## Testes e validação

Os testes em `tests/` incluem configurações fictícias e geram perfis/arquivos temporários nas saídas de compilação; limpe essas saídas antes de compartilhar o projeto.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project tests/TunnelChecks/TunnelChecks.csproj
& 'C:\Program Files\dotnet\dotnet.exe' run --project tests/ProtonChecks/ProtonChecks.csproj
& 'C:\Program Files\dotnet\dotnet.exe' run --project tests/PortalChecks/PortalChecks.csproj -- --header-only
# Dentro de src/ProtonBridge, com Go 1.26.6:
go test ./...
```

Foram verificados cancelamento do túnel, fechamento do proxy, ausência de motor órfão nos cenários testados, DPAPI, TOTP com dados fictícios, filtragem pelo plano, downloads do portal, temas e atraso do hover. Um teste real com sessão Proton salva confirmou configuração automática, conexão WireGuard e carregamento do Discord com o mesmo IP no navegador. Isso não equivale a uma auditoria de segurança.

`ProtonChecks.exe --live` é opcional e usa a sessão Proton salva do usuário do Windows; renova o token protegido e cria uma conexão real. Não é necessário executá-lo para usar o aplicativo.

## Componentes e licenças

- [wiresocks](https://github.com/shahradelahi/wiresocks), commit `a96360bb4369665ee31668d7de765d7f611a69fa`: avisos em `THIRD-PARTY-wiresocks.txt`.
- Helper derivado de [protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen), commit `32e869e3dbb48f135dc47a6fa2bb68834b2ea2ce`, sob GPL-3.0. Fontes, alterações e licença em `src/ProtonBridge` e, no pacote pronto, `ProtonBridge-source`. Preserve esses arquivos na redistribuição.
- .NET, WebView2 e bibliotecas transitivas mantêm suas próprias licenças. As licenças dos helpers não devem ser interpretadas como uma licença única de todo o aplicativo.
