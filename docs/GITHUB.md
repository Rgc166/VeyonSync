# Desenvolvimento e publicação no GitHub

Este repositório já está criado. Estas instruções são para clonar, compilar, contribuir e publicar versões.

## Clonar e compilar

Requisitos no computador de desenvolvimento:

- Windows x64;
- Git;
- .NET SDK 10 x64;
- Visual Studio 2022 (opcional, para abrir a solução visualmente).

Clone o repositório:

~~~powershell
git clone https://github.com/Rgc166/VeyonSync.git
cd VeyonSync
~~~

Abra <code>VeyonSync.sln</code> no Visual Studio ou compile pelo terminal:

~~~powershell
dotnet restore VeyonSync.sln
dotnet build VeyonSync.sln --configuration Release
dotnet test tests/VeyonSync.Tests/VeyonSync.Tests.csproj --configuration Release
~~~

Para gerar a pasta de instalação em <code>dist\Pendrive\VeyonSync</code>, execute:

~~~powershell
.\BUILD.bat
~~~

A pasta <code>dist</code>, além de <code>bin</code> e <code>obj</code>, é descartável e não deve ser versionada.

## Fazer alterações

1. Crie uma branch para a tarefa.
2. Faça as alterações e execute build/testes localmente.
3. Confira <code>git status</code> e confirme que não está enviando arquivos gerados, logs ou segredos.
4. Envie a branch e abra um Pull Request para <code>main</code>.
5. O GitHub Actions executa build e testes em Windows no Pull Request.

Antes do commit, confira que não aparecem:

- <code>dist/</code>, <code>bin/</code> ou <code>obj/</code>;
- <code>*.key</code> ou <code>*.pfx</code>;
- certificados, tokens, dados de laboratórios ou arquivos de ligação <code>.vsync</code>.

## Publicar uma versão para usuários

A publicação oficial deve ser feita no computador protegido do mantenedor, que contém a chave privada do publicador.

1. Instale a [GitHub CLI](https://cli.github.com).
2. Execute <code>gh auth login</code>.
3. Faça backup seguro de <code>%USERPROFILE%\.veyonsync-release</code>.
4. Revise a versão em <code>Directory.Build.props</code> e as notas da versão.
5. Execute <code>PUBLICAR_UPDATE.bat</code>.
6. Escolha gerar e publicar, revise os artefatos e digite <code>PUBLICAR</code> para confirmar.

A Release entrega o instalador completo, os dois pacotes assinados THC/STD e <code>SHA256SUMS.txt</code>. O pacote completo inclui o tutorial offline.

A chave privada fica em <code>%USERPROFILE%\.veyonsync-release\publisher.key</code>, protegida pelo Windows. Nunca a coloque no repositório, em Actions, nos ZIPs ou nos computadores dos laboratórios.

## Documentação relacionada

- [Guia de instalação para usuários](GUIA-INSTALACAO.md)
- [Guia de atualização pelo GitHub](GUIA-ATUALIZACAO.md)
- [Publicação e distribuição](DISTRIBUICAO.md)
