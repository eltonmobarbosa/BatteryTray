# BatteryTray

Mostra na área de notificação do Windows a bateria de mouses, teclados, headsets e controles sem fio — inclusive dos que o Windows não mostra sozinho (dongles 2.4G).

Cada dispositivo ganha o próprio ícone: o desenho do tipo (mouse, teclado, headset, controle) com uma bateria vertical que enche de baixo para cima. A bateria fica vermelha em 20% ou menos e na cor de destaque do Windows quando está carregando.

- **Clique esquerdo:** painel com todos os dispositivos, porcentagem e estado de carga (rola com a roda do mouse quando não cabem na tela).
- **Clique direito:** Atualizar, Iniciar com o Windows, Abrir log, Sair.
- **Aviso de bateria fraca:** notificação do Windows quando um dispositivo chega a 20%.
- **Na hora:** conectar, desconectar ou parear um dispositivo atualiza em ~2 s; a bateria em si é relida a cada minuto.
- Segue o tema claro/escuro da barra de tarefas e a cor de destaque.

## Download

Baixe o `BatteryTray.zip` da [última versão](https://github.com/eltonmobarbosa/BatteryTray/releases/latest), extraia o `BatteryTray.exe` numa pasta fixa e abra. Não precisa instalar nada. Ao abrir, uma notificação confirma que está funcionando e mostra o que foi encontrado. Cada versão é compilada pelo GitHub Actions a partir do código deste repositório.

Usa cerca de 13 MB de RAM.

## Como a bateria é lida

O app não depende de marca. Ele tenta, em ordem, várias formas de leitura; a mais específica vence quando duas leem o mesmo dispositivo.

| Leitor | Cobre | Testado com aparelho |
|---|---|---|
| HID padrão | Qualquer marca que publique a bateria pelo padrão HID (Battery Strength / Absolute State Of Charge), em relatório de feature ou de entrada | Não |
| Bluetooth | Qualquer dispositivo cuja bateria o Windows já lê (BLE Battery Service, headsets HFP); só os conectados | Não |
| Controles | Controles sem fio via Windows.Gaming.Input (Xbox e compatíveis) | Não |
| Compx | Chip Compx (VID 25A7), usado por Redragon e outras marcas, no dongle e no cabo | Sim (Redragon M711-PRO) |
| Logitech | HID++ 2.0 (Unifying, Lightspeed, Bolt e com fio) e HID++ 1.0 (Unifying antigos) | Não |
| Razer | Protocolo de 90 bytes documentado pelo OpenRazer | Não |
| Fantech | Kanata WG9RD, protocolo documentado em [peripheral-battery](https://github.com/MarcoVilera/peripheral-battery) | Não |
| HeadsetControl | Headsets SteelSeries, Corsair, HyperX, Logitech G e outros, via [HeadsetControl](https://github.com/Sapd/HeadsetControl) (coloque `headsetcontrol.exe` ao lado do app ou no PATH) | Não |

Não existe um protocolo universal para dongles 2.4G: cada chip fala de um jeito, então um dispositivo que não seguir nenhum dos acima não aparece. Veja abaixo como pedir suporte.

## Uso

Requer Windows 10 ou 11.

```powershell
dotnet run                    # app na bandeja
dotnet run -- --list          # leituras no terminal
dotnet run -- --diagnose      # relatório para pedir suporte a um dispositivo
dotnet run -- --preview a.png # folha com todos os ícones, para conferir o design
```

### Gerar o executável

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Gera `publish\BatteryTray.exe` (~135 MB; ~50 MB em zip), que roda sem precisar instalar o .NET. Não use `EnableCompressionInSingleFile`: o exe fica menor, mas descompacta tudo na RAM e o app passa de ~13 MB para ~60 MB. Coloque-o numa pasta fixa antes de marcar "Iniciar com o Windows": o atalho de inicialização aponta para esse caminho.

### Manter o ícone visível

- **Windows 11:** os ícones são fixados na barra automaticamente na primeira vez.
- **Windows 10:** arraste o ícone da setinha (^) para a barra uma vez; o Windows lembra.

Se o `.exe` mudar de pasta, o Windows trata os ícones como novos e é preciso arrastar de novo.

### Log

Erros, dispositivos que entram/saem e avisos de bateria ficam em `%LOCALAPPDATA%\BatteryTray\BatteryTray.log` (no máximo ~512 KB; o anterior vira `.log.old`). Clique direito → **Abrir log**.

### Microsoft Defender e SmartScreen

O executável ainda não tem assinatura digital, então o Windows pode mostrar "Editor desconhecido" ao abrir, e "Iniciar com o Windows" (que grava em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) às vezes dispara um falso positivo heurístico do Defender. O código completo está neste repositório e cada `.exe` dos Releases é compilado a partir dele pelo GitHub Actions.

A solução definitiva é assinar o executável; projetos open source podem pedir certificado gratuito à [SignPath Foundation](https://signpath.org/).

### Publicar uma versão

```powershell
git tag v1.2.3
git push origin v1.2.3
```

O workflow `.github/workflows/build.yml` compila, roda os self-checks e cria o Release com o `.exe`.

## Pedir suporte a um dispositivo

1. Conecte o dispositivo e rode `BatteryTray.exe --diagnose > diagnose.txt`.
2. Abra uma issue com o arquivo, o modelo exato e, se houver, o link do software oficial do fabricante.

O protocolo costuma ser descoberto observando o software oficial: muitos são apps Electron com o JavaScript legível, e nos outros dá para registrar as chamadas HID que eles fazem (foi assim que o leitor Compx foi feito).

## Licença

MIT
