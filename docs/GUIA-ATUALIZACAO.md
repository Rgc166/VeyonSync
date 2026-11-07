# Guia de atualização do Veyon Sync

O Veyon Sync usa o GitHub Releases como canal oficial de atualizações. O THC (Professor) consulta o GitHub. O STD (Aluno) recebe atualizações do professor ou por arquivo.

> [!IMPORTANT]
> Buscar uma versão nova não instala nada automaticamente. A instalação é iniciada por uma ação do administrador e os pacotes são validados antes de serem aplicados.

## Atualizar o THC pelo GitHub

1. Abra **Veyon Sync THC - Professor** com uma conta administradora.
2. Abra **Atualizações**.
3. Em **Configurações**, confira o repositório <code>Rgc166/VeyonSync</code>. Nas versões compiladas com o repositório padrão, esse campo já vem preenchido.
4. Clique em **Buscar no GitHub**.
5. Confira a versão e as notas da atualização.
6. Clique em **Baixar e instalar** e confirme.
7. Aguarde o serviço e a interface reiniciarem. Depois, confira a versão instalada e o resultado.

A busca automática, quando habilitada, apenas consulta e avisa sobre uma versão disponível. A instalação continua manual.

## Atualizar os alunos pelo THC

1. Atualize primeiro o THC.
2. Na área **Atualizações**, abra a aba **Computadores do laboratório**.
3. Use **Baixar do GitHub** para obter o pacote STD da Release mais recente.
4. Confira a versão e escolha computadores de teste.
5. Distribua o pacote pela rede.
6. Acompanhe transferência, instalação e confirmação de cada computador.

O STD não precisa acessar o GitHub nem armazenar token. O professor baixa o pacote e distribui aos agentes pareados.

## Atualização por arquivo (sem internet)

Também é possível utilizar os pacotes gerados pelo processo de publicação:

- <code>VeyonSync-THC-Update-&lt;versão&gt;.zip</code> para o professor;
- <code>VeyonSync-STD-Update-&lt;versão&gt;.zip</code> para os alunos;
- pacote de recuperação para verificação e restauração manual.

No THC ou STD, abra **Atualizações → Atualizar por arquivo** e selecione o ZIP específico do produto. O sistema recusa pacotes destinados ao outro produto.

## O que é validado antes da instalação?

- Assinatura ECDSA P-256 do publicador.
- Identificação do produto (THC ou STD).
- Arquitetura do Windows.
- Versão e compatibilidade mínima.
- Tamanho e hash SHA-256 do conteúdo.
- Integridade e caminhos do arquivo compactado.

Pacotes com assinatura inválida, conteúdo alterado, produto incorreto ou versão incompatível são recusados.

## Recuperação e logs

O atualizador mantém backups e tenta restaurar a versão anterior quando a instalação não conclui a verificação de inicialização. Não desligue o computador durante a aplicação.

- Professor: <code>C:\ProgramData\VeyonSync\Teacher\logs\</code>
- Aluno: <code>C:\ProgramData\VeyonSync\Student\logs\</code>

Para detalhes de compatibilidade e recuperação manual, consulte [ATUALIZACOES.txt](ATUALIZACOES.txt).
