# Guia de instalação do Veyon Sync

Este guia é destinado a quem vai instalar e utilizar o Veyon Sync. Não exige Visual Studio, Git ou .NET SDK no computador do laboratório.

> [!IMPORTANT]
> Use um pacote completo da página **GitHub → Releases**. O botão **Code → Download ZIP** baixa o código-fonte, não o instalador.

## Requisitos

- Windows x64.
- Permissão de administrador para instalar serviços e configurar o Firewall do Windows.
- Rede local permitindo comunicação entre os computadores dos alunos e o computador do professor.
- Veyon instalado e configurado no computador que executará o THC.
- Um computador designado como professor por laboratório.

O .NET SDK 10 é necessário somente para quem compila o projeto. Os pacotes publicados incluem o runtime.

## 1. Baixar e preparar

1. Abra [Releases do Veyon Sync](https://github.com/Rgc166/VeyonSync/releases).
2. Na versão estável mais recente, baixe **VeyonSync-Instalacao-win-x64.zip**.
3. Extraia o ZIP para uma pasta local ou para um pendrive.
4. Confirme que a pasta contém <code>INICIAR.bat</code>, <code>LEIA-ME.txt</code>, <code>Teacher</code>, <code>Student</code> e <code>scripts</code>.

## 2. Instalar o professor (THC)

Faça esta etapa uma vez por laboratório:

1. No computador do professor, abra a pasta extraída.
2. Execute <code>INICIAR.bat</code> e aceite a solicitação de administrador.
3. Escolha **[1] Instalar professor**.
4. Informe a identificação do laboratório (por exemplo, A, B ou C).
5. Ao terminar, abra **Veyon Sync THC - Professor** pelo Menu Iniciar ou atalho.
6. Entre em **Credenciamento** e gere o arquivo de ligação <code>.vsync</code>.
7. Salve esse arquivo na pasta <code>Ligacoes</code> do pendrive.

O instalador configura o serviço do professor, o certificado HTTPS e uma regra de Firewall limitada à sub-rede local e aos perfis Domain/Private.

## 3. Instalar os alunos (STD)

Em cada computador de aluno:

1. Copie a pasta completa do pacote para o computador ou conecte o pendrive.
2. Execute <code>INICIAR.bat</code> como administrador.
3. Escolha **[2] Instalar aluno**.
4. Quando solicitado, selecione o arquivo <code>.vsync</code> do laboratório correto.
5. No THC do professor, gere um código de credenciamento temporário.
6. No STD, informe o número do computador e o código gerado.
7. Clique em **Validar, parear e enviar**.

O código é temporário, tem uso único e deve ser gerado novamente quando expirar ou já tiver sido utilizado.

## 4. Conferir e integrar com o Veyon

1. No THC, confira se os computadores aparecem no inventário.
2. Abra **Integração Veyon** e selecione **Prévia**.
3. Analise computadores novos, atualizados e conflitos.
4. Faça um teste com poucos computadores.
5. Somente depois da conferência, confirme a integração.

O THC faz backup antes de aplicar alterações. Conflitos não devem ser forçados: corrija-os no Veyon Configurator e execute uma nova prévia.

## 5. Segurança e privacidade

O STD envia ao professor dados necessários à identificação do computador, incluindo nome da máquina, usuário, IP e MAC. Utilize o sistema somente em equipamentos e redes que você tenha autorização para administrar.

Antes de compartilhar logs ou capturas de tela, remova nomes de usuários, IPs, MACs, códigos de credenciamento, certificados e outras informações do laboratório.

## 6. Problemas comuns

| Problema | O que conferir |
|---|---|
| Professor inacessível | PC ligado, mesma rede, nome do professor resolvendo na rede e Firewall |
| Arquivo de ligação recusado | Se o arquivo pertence ao laboratório e ao THC correto |
| Código inválido | Gere um novo código no THC; códigos são de uso único |
| Serviço não inicia | Consulte os logs da instalação e do serviço |
| Computador não aparece | Confira o pareamento e a última comunicação no THC |
| Conflito na prévia do Veyon | Corrija o registro existente no Veyon Configurator antes de tentar novamente |

Os logs ficam em <code>C:\ProgramData\VeyonSync\</code> e na pasta <code>logs</code> do pacote de instalação.

## Outros guias

- [Atualizar pelo GitHub](GUIA-ATUALIZACAO.md)
- [Tutorial detalhado](TUTORIAL.txt)
- [Notas e compatibilidade das atualizações](ATUALIZACOES.txt)
