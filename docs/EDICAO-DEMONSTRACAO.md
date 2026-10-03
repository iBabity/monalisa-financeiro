# Edição de demonstração

A edição de demonstração foi criada para apresentações do Monalisa Financeiro.
Ela funciona como aplicativo nativo do Windows e não solicita chave de ativação,
não consulta o Painel Central e não executa a atualização obrigatória da edição
comercial.

## Gerar o instalador

Na raiz do projeto, execute:

```powershell
.\installer\package-showcase.ps1
```

O arquivo será criado em:

```text
artifacts\showcase\Monalisa-Financeiro-Demonstracao-Setup-1.1.16.exe
```

## Separação da edição comercial

A demonstração usa pastas próprias:

- programa: `C:\Program Files\Monalisa Financeiro Demonstracao`;
- dados: `C:\ProgramData\Monalisa Financeiro Demonstracao\data`;
- atalho: `Monalisa Financeiro Demonstracao`.

Assim, instalar ou remover a demonstração não substitui o banco nem os arquivos
de uma instalação comercial existente. No primeiro uso, a pessoa cria somente o
administrador geral. O cadastro `MERCADO DEMONSTRAÇÃO` já fica disponível para a
apresentação.

O marcador `showcase-edition.txt` existe somente dentro do pacote gerado. O
instalador comercial continua usando o licenciamento e a atualização obrigatória.

## Repositório privado

O instalador e os bancos locais não entram no Git. As regras do `.gitignore`
excluem `artifacts`, `data`, `backups`, arquivos SQLite, chaves privadas e pastas
temporárias. Antes de enviar alterações, confirme que o repositório remoto está
marcado como privado e revise `git status`.
