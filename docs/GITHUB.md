# Subir o projeto no GitHub

1. Crie uma conta em https://github.com e instale o Git: https://git-scm.com/download/win
2. No GitHub: New repository > nome `veyonsync` > escolha Public ou Private > NAO marque README/.gitignore > Create.
   (Atualizacao pelo GitHub Releases exige repositorio PUBLICO; privado nao e suportado pelo programa.)
3. Na pasta do projeto (onde esta o BUILD.bat), abra o PowerShell e rode:

```powershell
git init
git config user.name "Seu Nome"
git config user.email "seu@email.com"
git add .
git status
git commit -m "Veyon Sync 2.2.0"
git branch -M main
git remote add origin https://github.com/SEU_USUARIO/veyonsync.git
git push -u origin main
```
4. O login abre no navegador na primeira vez.
5. Antes do `git commit`, confira no `git status` que NAO aparecem `dist/`, `bin/`, `obj/`, `*.key` ou `*.pfx` (o `.gitignore` ja bloqueia).
   A chave privada fica em `%USERPROFILE%\.veyonsync-release`, fora do projeto, e nunca deve ir ao repositorio.
   `assets/release-public-key.txt` (chave publica) pode ir.
6. Proximas alteracoes: `git add .` > `git commit -m "o que mudou"` > `git push`.
7. Publicar versao para os PCs: instale a GitHub CLI (https://cli.github.com), rode `gh auth login` e depois PUBLICAR_UPDATE.bat.
   No THC: Atualizacoes > Configuracoes > repositorio `SEU_USUARIO/veyonsync`.
