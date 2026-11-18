# BatteryTray

Mostra na área de notificação do Windows a bateria de mouses, teclados, headsets e controles sem fio — inclusive dos que o Windows não mostra sozinho (dongles 2.4G).

Cada dispositivo ganha o próprio ícone: o desenho do tipo (mouse, teclado, headset, controle) com uma bateria vertical que enche de baixo para cima. A bateria fica vermelha em 20% ou menos e na cor de destaque do Windows quando está carregando.

- **Clique esquerdo:** painel com todos os dispositivos, porcentagem e estado de carga.
- **Clique direito:** Atualizar, Iniciar com o Windows, Sair.
- **Aviso de bateria fraca:** notificação do Windows quando um dispositivo chega a 20%.
- Segue o tema claro/escuro da barra de tarefas e a cor de destaque.

## Como a bateria é lida

O app não depende de marca. Ele tenta, em ordem, várias formas de leitura; a mais específica vence quando duas leem o mesmo dispositivo.

| Leitor | Cobre | Testado com aparelho |
|---|---|---|
| HID padrão | Qualquer marca que publique a bateria pelo padrão HID (Battery Strength / Absolute State Of Charge) | Não |
| Bluetooth | Qualquer dispositivo cuja bateria o Windows já lê (BLE Battery Service, headsets HFP); só os conectados | Não |
| Controles | Controles sem fio via Windows.Gaming.Input (Xbox e compatíveis) | Não |
| Compx | Chip Compx (VID 25A7), usado por Redragon e outras marcas, no dongle e no cabo | Sim (Redragon M711-PRO) |
| Logitech | HID++ 2.0: receptores Unifying, Lightspeed, Bolt e dispositivos com fio | Não |
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
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Gera `publish\BatteryTray.exe` (~55 MB), que roda sem precisar instalar o .NET. Coloque-o numa pasta fixa antes de marcar "Iniciar com o Windows": o atalho de inicialização aponta para esse caminho.

### Manter o ícone visível

- **Windows 11:** os ícones são fixados na barra automaticamente na primeira vez.
- **Windows 10:** arraste o ícone da setinha (^) para a barra uma vez; o Windows lembra.

Se o `.exe` mudar de pasta, o Windows trata os ícones como novos e é preciso arrastar de novo.

### Microsoft Defender

"Iniciar com o Windows" grava em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Em executáveis sem assinatura digital isso às vezes dispara um falso positivo heurístico do Defender. O código completo está neste repositório; a solução definitiva é assinar o executável.

## Pedir suporte a um dispositivo

1. Conecte o dispositivo e rode `BatteryTray.exe --diagnose > diagnose.txt`.
2. Abra uma issue com o arquivo, o modelo exato e, se houver, o link do software oficial do fabricante.

O protocolo costuma ser descoberto observando o software oficial: muitos são apps Electron com o JavaScript legível, e nos outros dá para registrar as chamadas HID que eles fazem (foi assim que o leitor Compx foi feito).

## Licença

MIT
