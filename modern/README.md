# vmPing Modern — preview 0.1

Aplicativo desktop em português para Windows e Linux. Evolução independente do fluxo do [vmPing de Ryan Smith](https://github.com/r-smith/vmPing), com uma interface Avalonia com navegação lateral e um novo núcleo de rede sem dependências de WPF. O código original permanece em `../vmPing` e não foi alterado. Esta preview ainda não substitui todos os recursos da versão original.

## Executar

- **Windows x64:** extraia todo o ZIP e abra `vmPing-modern.exe`. O runtime .NET está incluído. O executável ainda não tem assinatura digital.
- **Linux x64:** extraia o `.tar.gz` e execute `./vmPing-modern`. O runtime .NET está incluído. Requer uma sessão gráfica X11 ou XWayland e bibliotecas nativas do Avalonia. Em Ubuntu/Debian, instale `libx11-6 libice6 libsm6 libfontconfig1 libgl1 iputils-ping` se faltarem.

Apenas adicionar ou carregar hosts não inicia tráfego. Clique em **Iniciar todos** ou no botão do painel.

## Recursos implementados

- Monitoramento simultâneo ICMP e TCP, com início/parada por host ou em conjunto.
- Latência ICMP com precisão abaixo de 1 ms quando o sistema permite (veja abaixo) e jitter por host.
- Histerese de estado: o host só fica offline após N falhas seguidas e só volta após M respostas (padrão 3 e 1, em Configurações). Evita alertas por perda isolada.
- Nomes DNS resolvidos uma vez e renovados a cada 5 minutos; se a renovação falhar, o último endereço conhecido continua sendo testado. Falhas inesperadas viram "erro local" e o monitoramento continua.
- Intervalo, tempo limite e limites de falha alterados em Configurações valem imediatamente para hosts em execução.
- Alertas no Telegram nas mudanças de estado, agrupados em janelas de 5 segundos (útil quando um POP inteiro cai).
- Inclusão em lote com nome por item: `10.0.0.1 OLT-Centro, 10.0.0.2 OLT-Norte`.
- IPv4, IPv6 e nomes DNS. Para TCP com IPv6, use `[::1]:443`; sem porta, `::1` usa ICMP.
- Painéis de status responsivos, resumo dos hosts, min/média/máx, perda, erros locais e gráfico em linha das últimas 60 sondagens.
- Temas claro e escuro, aliases na inclusão de hosts e favoritos de conjuntos de hosts na tela Configurações.
- Histórico de mudanças de estado gravado em `history.log` (rotação em 5 MB); os últimos 2.000 eventos são recarregados ao abrir.
- Exportação CSV dos contadores (com status e jitter), resultados MTR (com roteadores ECMP) e eventos. O histórico detalhado de cada pacote não é gravado em disco.
- Traceroute ICMP IPv4 e IPv6, com resolução direta do destino e DNS reverso dos roteadores que respondem.
- MTR contínuo: percorre TTLs sequencialmente e repete a rota até você clicar em **Parar**. Exibe enviados, perda e último/média/mínimo/máximo por salto.
- Importação de favoritos e aliases do `vmPing.xml` original, inclusive do formato da release 1.3.23.

## Traceroute e MTR

1. Abra **Rotas & MTR** no menu lateral.
2. Informe um IP ou nome DNS, sem porta, e escolha **IPv4** ou **IPv6**. Um nome com registros A e AAAA usa a família escolhida.
3. Marque **MTR contínuo** para repetir a rota; desmarque para uma execução.
4. Ajuste o máximo de saltos. Intervalo e timeout vêm dos controles da tela **Configurações** e são capturados ao iniciar.
5. Clique em **Iniciar**. **Parar** cancela as sondagens e preserva os resultados para exportação.

O DNS reverso depende de registros PTR publicados; um endereço sem PTR continua aparecendo pelo IP. A primeira consulta reversa tem limite de 700 ms e o resultado é armazenado até a próxima execução. A resolução reversa não entra no RTT exibido.

O intervalo do MTR representa o mínimo entre inícios de ciclos completos. Cada ciclo testa um salto por vez até o destino ou o limite de TTL; vários timeouts podem tornar um ciclo maior que o intervalo. Não há sobreposição de ciclos. Os tempos de respostas `TTL exceeded` são medidos por relógio monotônico, pois `PingReply.RoundtripTime` não fornece RTT válido para esse estado. Esses tempos intermediários são estimativas que incluem o custo da API e, no Linux sem privilégios, do processo de ping. Podem superestimar a latência real. No destino, usamos o RTT informado pelo sistema.

Perda em salto intermediário pode resultar de limitação de ICMP, mesmo quando o destino funciona. Não representa necessariamente perda de tráfego encaminhado. Saltos sem resposta exibem `*` inicialmente. Após uma resposta, o último endereço conhecido permanece com o status da sondagem atual. Com balanceamento ECMP, cada salto acumula todos os roteadores que responderam naquele TTL: o subtítulo indica "ECMP: N roteadores" e a dica do mouse lista cada IP com sua contagem de respostas. Os contadores do salto somam todos os caminhos, como no `mtr`.

Os TTLs são testados um de cada vez. No Linux, sondas `System.Net.Ping` simultâneas recebem as respostas `TTL exceeded` umas das outras e atribuem roteadores ao salto errado, por isso não há envio paralelo.

A família de rede é explícita no traceroute/MTR. A indisponibilidade de uma família gera mensagem de erro. Não há troca silenciosa de IPv6 para IPv4. Uma máquina sem conectividade IPv6 externa só poderá testar destinos IPv6 acessíveis localmente.

## Dados e compatibilidade

Configuração nova: `%LOCALAPPDATA%/vmPing-modern/workspace.json` no Windows e no diretório de dados locais do usuário no Linux (normalmente `~/.local/share/vmPing-modern/workspace.json`). Hosts, favoritos, intervalos e tema são salvos; sondagens não reiniciam automaticamente ao abrir.

O XML original é apenas lido. Credenciais SMTP, cores e layout de colunas antigos não são importados. Favoritos com nome repetido recebem sufixo; favoritos já existentes são preservados. Os aliases importados também são usados ao adicionar um endereço conhecido sem fornecer outro alias.

A gravação usa arquivo temporário e substituição; o JSON anterior fica em `workspace.json.bak`. Um JSON inválido desativa o salvamento daquela execução para preservar o arquivo. Feche o aplicativo e restaure o `.bak` manualmente nesse caso. Para voltar ao vmPing original, abra seu executável e a configuração antiga intacta.

O token do bot do Telegram é salvo sem criptografia no `workspace.json` do usuário. Use um bot dedicado a alertas.

Sobre a precisão: `PingReply.RoundtripTime` só tem milissegundos inteiros. O app mede o tempo localmente e usa essa medida quando ela cai dentro do mesmo milissegundo informado pelo sistema; caso contrário (por exemplo, no Linux sem permissão de ICMP, quando o .NET executa o processo `ping`), mantém o valor do sistema. Para precisão abaixo de 1 ms no Linux sem root, permita sockets ICMP: `sudo sysctl -w net.ipv4.ping_group_range="0 2147483647"`.

Ainda não foram portados: alertas de e-mail/áudio, notificações do sistema, bandeja, gerador de pacotes, gerenciamento completo de aliases/favoritos, persistência do histórico e todas as opções avançadas de ICMP. A interface usa painéis com quebra automática, sem o seletor de colunas original. Não há atualização automática.

## Desenvolvimento

Requer .NET SDK 10. Dependências Avalonia fixadas em 11.3.22.

```bash
dotnet build VmPing.Modern.slnx -c Release
dotnet test VmPing.Modern.slnx -c Release
dotnet run --project src/VmPing.Desktop
```

```bash
dotnet publish src/VmPing.Desktop -c Release -r win-x64 --self-contained true -o ../dist/windows-x64
dotnet publish src/VmPing.Desktop -c Release -r linux-x64 --self-contained true -o ../dist/linux-x64
```

Os testes incluem sondagens reais de loopback IPv4/IPv6 e TCP; exigem suporte ICMP local. A interface também é testada por Avalonia Headless. Testes não precisam de internet. No Linux, a disponibilidade de ICMP depende das permissões e da configuração de rede do sistema; falhas aparecem como erros locais.

Validação nesta entrega: build Release sem avisos; 28 testes; GUI Linux em Xvfb; traceroute IPv4 externo com respostas intermediárias e PTR. Windows foi compilado, mas ainda precisa ser executado em uma máquina Windows. IPv6 foi testado em loopback, sem prova de rota IPv6 externa.

Licença MIT. Consulte `../LICENSE`. Projeto original: Copyright (c) 2020 Ryan Smith.
