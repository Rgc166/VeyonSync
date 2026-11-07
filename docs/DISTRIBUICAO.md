# Publicação e distribuição do Veyon Sync

Este documento é para o mantenedor do projeto. A publicação deve ser feita no computador de desenvolvimento que possui a chave privada do publicador.

## O que cada Release entrega

- <code>VeyonSync-Instalacao-win-x64.zip</code>: instalação completa para novas escolas, com THC, STD, menu do pendrive e tutorial offline.
- <code>VeyonSync-THC-x.y.z-win-x64.vsupd</code>: atualização assinada do professor.
- <code>VeyonSync-STD-x.y.z-win-x64.vsupd</code>: atualização assinada do aluno.
- <code>SHA256SUMS.txt</code>: hashes SHA-256 dos três arquivos.

O ZIP de instalação contém os programas publicados por <code>scripts/build.ps1</code>. Não contém código-fonte, credenciais de laboratório ou a chave privada do publicador.

## Preparação única

1. Mantenha <code>assets/github-repo.txt</code> com o texto <code>Rgc166/VeyonSync</code>. Esse valor é incorporado aos aplicativos durante a compilação.
2. Confirme que <code>assets/release-public-key.txt</code> corresponde à chave pública da chave privada em <code>%USERPROFILE%\.veyonsync-release\publisher.key</code>.
3. Faça backup seguro de toda a pasta <code>%USERPROFILE%\.veyonsync-release</code>. Sem a chave privada original, os computadores já instalados não aceitarão atualizações futuras assinadas por outra chave.
4. Instale .NET SDK 10 x64 e GitHub CLI.
5. Execute <code>gh auth login</code> e confirme que a conta tem permissão para publicar Releases neste repositório.

Nunca envie <code>publisher.key</code>, certificados privados, tokens ou credenciais ao GitHub.

## Publicar uma versão

1. Atualize o número em <code>Directory.Build.props</code> ou informe uma versão válida ao <code>PUBLICAR_UPDATE.bat</code>.
2. Revise o histórico de mudanças e prepare notas claras.
3. Execute <code>PUBLICAR_UPDATE.bat</code>.
4. Informe a versão e as notas; escolha **Gerar e publicar no GitHub Releases**.
5. Revise os nomes e produtos dos pacotes.
6. Digite <code>PUBLICAR</code> somente depois de conferir tudo.

O script compila a ferramenta de pacotes, compila e testa o produto, gera o instalador completo, monta os dois pacotes assinados, verifica as assinaturas, gera os hashes e só então envia os artefatos à Release.

Versões com sufixo, como <code>2.3.0-beta.1</code>, são pré-lançamentos. Não são a versão estável oferecida por <code>releases/latest</code>.

## Validar após publicar

1. Abra [Releases](https://github.com/Rgc166/VeyonSync/releases).
2. Confirme que os quatro arquivos esperados estão anexados.
3. Baixe o ZIP de instalação em outro computador e confira <code>INICIAR.bat</code> e <code>LEIA-ME.txt</code>.
4. Instale em um ambiente de teste.
5. No THC, confira **Buscar no GitHub** e instale uma atualização de teste.
6. Distribua uma atualização STD para um aluno de teste.
7. Só divulgue a Release depois de validar instalação, pareamento e atualização.

## Limite da automação

O GitHub Actions executa build e testes em Windows a cada push ou Pull Request. Ele não assina nem publica pacotes, porque a chave privada deve permanecer no computador protegido do mantenedor.
