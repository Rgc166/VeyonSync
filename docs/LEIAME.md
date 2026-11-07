# Veyon Sync 2.0 — THC (Professor) e STD (Aluno)

Evolução da Etapa 1 (serviços professor + aluno). Tutorial de uso: `docs/TUTORIAL.txt` (vai para o pendrive como `LEIA-ME.txt`).

## Estrutura

```text
Shared/       Modelos, arquivo de ligação (.vsync), TLS com certificado fixado, IPC por Named Pipe,
              assinatura de atualizações, exportadores CSV/TXT/PDF/DOCX
Teacher/      Serviço VeyonSyncTeacher (ASP.NET Core + SQLite + veyon-cli)
Student/      Serviço VeyonSyncStudent (coleta, fila, envio, pareamento, atualizador auxiliar)
Desktop/      Tema e componentes WPF compartilhados
TeacherApp/   Veyon Sync THC — interface do professor (WPF, exige administrador)
StudentApp/   Veyon Sync STD — interface do aluno (WPF, exige administrador)
tests/        Testes automatizados (xUnit)
scripts/      build, instalação, atualização e remoção
pendrive/     INICIAR.bat e textos das pastas do pendrive
assets/icons/ thc.ico e std.ico (ícones definitivos, quando fornecidos)
```

## Compilar

Requisito único: .NET SDK 10 (x64) no PC que compila. Os pacotes são autocontidos; os laboratórios não precisam de runtime.

```bat
BUILD.bat            (testes + publicação; resultado em dist\Pendrive\VeyonSync)
```

## Decisões principais

- Nomes internos da Etapa 1 mantidos: serviços `VeyonSyncTeacher`/`VeyonSyncStudent`, pastas
  `C:\Program Files\VeyonSync\<Teacher|Student>` e `C:\ProgramData\VeyonSync\<Teacher|Student>`,
  `appsettings.json`, `config.json`, `state.json`, `teacher.db`. Só os nomes exibidos mudaram.
- Banco: migração aditiva para o esquema 2 com cópia `teacher.db.antes-v2-*.bak` antes de migrar.
- TLS: o STD fixa o certificado do professor (SHA-256 vindo do `.vsync`) e confere validade, uso de servidor
  e o nome do endereço contra os nomes válidos (SAN DNS/IP, depois CN). Nada é instalado como raiz do Windows.
  Instalações da Etapa 1 (certificado importado em Raiz) continuam com a validação padrão até importar um `.vsync`.
- IPC: pipes `VeyonSync.Teacher.v2`/`VeyonSync.Student.v2` com ACL Administradores + SYSTEM; o serviço confere
  a elevação do cliente por personificação.
- Veyon: somente `help`, `networkobjects export/add/remove` e `config export/import`. Atualizar = remover o
  registro antigo (por nome, só quando o nome é único no diretório) + adicionar o novo, com releitura ao final.
  Nunca usa `networkobjects clear`. Registros sem associação inequívoca viram conflito.
- Lote multi-laboratório: o THC central chama `/api/peer/veyon/apply` de cada professor autorizado
  (certificado fixado + token de professor). Cada professor aplica só o próprio laboratório no próprio Veyon.
- Atualização pela rede: manifesto assinado (ECDSA P-256) pelo THC; o STD confere assinatura, SHA-256, versão,
  produto e emissor, extrai só `Service/` e `App/`, e roda uma cópia de si mesmo fora da pasta de instalação
  como atualizador auxiliar (backup, troca, reinício, confirmação de versão, reversão).
