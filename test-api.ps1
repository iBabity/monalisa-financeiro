param([string]$ApiDll = (Join-Path $PSScriptRoot 'api\bin\Debug\net10.0\Monalisa.Financeiro.Server.dll'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testDir = Join-Path $root ('test-results\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $testDir | Out-Null
$env:FINANCE_DB = Join-Path $testDir 'test.db'
$env:FINANCE_URL = 'http://127.0.0.1:5199'
$env:FINANCE_AUTH_DISABLED = '1'
$env:MONALISA_LICENSE_DISABLED = '1'
$process = Start-Process -FilePath 'C:\Tools\dotnet\dotnet.exe' -ArgumentList @($ApiDll) -WorkingDirectory (Join-Path $root 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDir 'api.log') -RedirectStandardError (Join-Path $testDir 'error.log')
function Request($path, $body = $null) {
    if ($null -eq $body) { return Invoke-RestMethod ('http://127.0.0.1:5199/api' + $path) }
    return Invoke-RestMethod ('http://127.0.0.1:5199/api' + $path) -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8)))
}
function Check($condition, $description) { if (-not $condition) { throw "FALHOU: $description" }; Write-Output "OK: $description" }
function Rejected($path, $body) { try { Request $path $body | Out-Null; throw 'Operação deveria ser recusada' } catch { if ($_.Exception.Response.StatusCode.value__ -notin @(400,409)) { throw } } }
try {
    for ($i=0; $i -lt 40; $i++) { try { Request '/health' | Out-Null; break } catch { Start-Sleep -Milliseconds 300 } }
    $today = Get-Date -Format 'yyyy-MM-dd'
    Request '/companies/1/parties' @{ name='Cliente de teste';document='';type='both' } | Out-Null
    Request '/companies/1/titles' @{ kind='receivable';description='Receita';amount=10000;due=$today;partyId=1;category='Serviços';notes='';installments=1 } | Out-Null
    $s = Request '/companies/1/state'
    Check ($s.accounts[0].balance -eq 0) 'Lançamento previsto não altera caixa'
    $settle = @{ accountId=1;principal=4000;interest=100;penalty=50;discount=200;date=$today }
    $receipt = Request '/companies/1/titles/1/settle' $settle
    $s = Request '/companies/1/state'
    Check ($s.titles[0].remaining -eq 6000 -and $s.accounts[0].balance -eq 3950) 'Baixa parcial com juros, multa e desconto'
    Rejected '/companies/1/titles/1/settle' @{ accountId=1;principal=6001;interest=0;penalty=0;discount=0;date=$today }
    Check ((Request '/companies/1/state').accounts[0].balance -eq 3950) 'Excesso recusado sem alterar saldo'
    Rejected '/companies/1/titles/1/cancel' @{reason='Ainda possui baixa'}
    Request "/companies/1/settlements/$($receipt.id)/reverse" @{reason='Correção de teste'} | Out-Null
    $s=Request '/companies/1/state'
    Check ($s.titles[0].remaining -eq 10000 -and $s.accounts[0].balance -eq 0) 'Estorno restaura principal e saldo'
    Rejected "/companies/1/settlements/$($receipt.id)/reverse" @{reason='Repetido'}
    Request '/companies/1/titles' @{ kind='payable';description='Parcelado';amount=10000;due='2026-01-31';category='Teste';notes='';installments=3 } | Out-Null
    $parts=@((Request '/companies/1/state').titles | Where-Object description -like 'Parcelado*')
    Check (($parts.amount | Measure-Object -Sum).Sum -eq 10000 -and $parts.Count -eq 3 -and $parts[1].due -eq '2026-02-28' -and $parts[2].due -eq '2026-03-31') 'Parcelas preservam total e fim do mês'
    Request '/companies/1/accounts' @{name='Banco';opening=50000} | Out-Null
    Request '/companies/1/transfers' @{fromId=2;toId=1;amount=10000;date=$today} | Out-Null
    $s=Request '/companies/1/state'
    Check (($s.accounts.balance | Measure-Object -Sum).Sum -eq 50000) 'Transferência preserva saldo consolidado'
    $c=Request '/companies' @{name='Outra empresa';cnpj='12345678000195'}
    Rejected '/companies/1/titles/1/settle' @{accountId=3;principal=100;interest=0;penalty=0;discount=0;date=$today}
    Rejected "/companies/$($c.id)/titles" @{kind='payable';description='Inválido';amount=100;due=$today;partyId=1;category='';notes='';installments=1}
    Check ((Request "/companies/$($c.id)/state").titles.Count -eq 0) 'Isolamento de empresas e referências'
    Request '/companies/1/titles/1/cancel' @{reason='Cancelado após estorno'} | Out-Null
    Rejected '/companies/1/titles/1/settle' $settle
    Check (@((Request '/companies/1/state').titles | Where-Object id -eq 1)[0].cancelled -eq 1) 'Cancelamento impede novas baixas'
    Request '/companies/1/titles/2/settle' @{accountId=1;principal=3334;interest=0;penalty=0;discount=0;date=$today} | Out-Null
    Check (@((Request '/companies/1/state').accounts | Where-Object id -eq 1)[0].balance -eq 6666) 'Pagamento reduz saldo da conta'
    foreach($kind in @('payable','receivable')) {
        $description='Grupo ' + $kind
        $input=@{kind=$kind;description=$description;amount=9000;due=$today;category='';notes='';installments=3}
        Request '/companies/1/titles' $input | Out-Null
        Request '/companies/1/titles' $input | Out-Null
        $groups=@((Request '/companies/1/state').titles | Where-Object description -like "$description*" | Group-Object installmentGroup)
        Check ($groups.Count -eq 2 -and $groups[0].Count -eq 3) "$kind : lançamentos idênticos possuem vínculos independentes"
        $first=@($groups[0].Group | Sort-Object installmentNumber)
        $other=@($groups[1].Group | Sort-Object installmentNumber)
        Request "/companies/1/titles/$($first[0].id)/settle" @{accountId=1;principal=100;interest=0;penalty=0;discount=0;date=$today} | Out-Null
        $result=Request "/companies/1/titles/$($first[1].id)/cancel" @{reason='Cancelar grupo de teste';scope='all_open'}
        $s=Request '/companies/1/state'
        $remaining=@($s.titles | Where-Object installmentGroup -eq $groups[0].Name)
        Check ($result.cancelledCount -eq 2 -and @($remaining | Where-Object cancelled -eq 1).Count -eq 2 -and @($s.titles | Where-Object { $_.installmentGroup -eq $groups[1].Name -and $_.cancelled -eq 1 }).Count -eq 0) "$kind : cancela só grupo escolhido e preserva parcela com baixa parcial"
        $result=Request "/companies/1/titles/$($other[0].id)/cancel" @{reason='Somente uma';scope='single'}
        Check ($result.cancelledCount -eq 1 -and @((Request '/companies/1/state').titles | Where-Object { $_.installmentGroup -eq $groups[1].Name -and $_.cancelled -eq 0 }).Count -eq 2) "$kind : cancelar só uma preserva as demais parcelas"
        Rejected "/companies/1/titles/$($other[1].id)/cancel" @{reason='Inválido';scope='arbitrary'}
        Rejected "/companies/$($c.id)/titles/$($other[1].id)/cancel" @{reason='Outra empresa';scope='all_open'}
    }
    Request '/companies/1/titles' @{kind='payable';description='Editar parcelas';amount=6000;due='2026-10-10';category='Antiga';notes='';installments=2} | Out-Null
    $editParts=@((Request '/companies/1/state').titles | Where-Object description -like 'Editar parcelas*' | Sort-Object installmentNumber)
    $edited=Request "/companies/1/titles/$($editParts[0].id)/edit" @{description='Aluguel';amount=3500;due='2026-11-15';category='Imóvel';notes='Reajustado';scope='all_open'}
    $editParts=@((Request '/companies/1/state').titles | Where-Object description -like 'Aluguel*' | Sort-Object installmentNumber)
    Check ($edited.editedCount -eq 2 -and $editParts[0].amount -eq 3500 -and $editParts[1].due -eq '2026-12-15') 'Edição em grupo mantém identificação e vencimentos mensais'
    $deleted=Request "/companies/1/titles/$($editParts[0].id)/delete" @{reason='Teste de exclusão';scope='all_open'}
    $s=Request '/companies/1/state'
    Check ($deleted.deletedCount -eq 2 -and @($s.titles | Where-Object description -like 'Aluguel*').Count -eq 0 -and @($s.audit | Where-Object action -eq 'Exclusão').Count -eq 2) 'Exclusão retira lançamentos e preserva auditoria'
    Request '/companies/1/titles/1/delete' @{reason='Baixa já estornada';scope='single'} | Out-Null
    Check (@((Request '/companies/1/state').titles | Where-Object id -eq 1).Count -eq 0) 'Lançamento pode ser excluído depois que todas as baixas foram estornadas'
    Request '/companies/1/parties/1/delete' @{} | Out-Null
    Check (@((Request '/companies/1/state').parties | Where-Object id -eq 1).Count -eq 0) 'Cadastro pode ser excluído após excluir seus lançamentos'
    Rejected '/companies/1/titles/2/delete' @{reason='Ainda possui baixa ativa';scope='single'}
    Rejected '/companies/1/titles/2/edit' @{description='Não pode';amount=100;due=$today;category='';notes='';scope='single'}
    Request '/companies/1/plans' @{name='AJUSTE TESTE';kind='payable';interestMode='value';interestValue=275;interestRate=0;penaltyMode='percentage';penaltyRate=0;penaltyValue=0;discountMode='percentage';discountRate=0;discountValue=0} | Out-Null
    Request '/companies/1/plans' @{name='AJUSTE TESTE';kind='receivable';interestMode='percentage';interestRate=0;interestValue=0;penaltyMode='percentage';penaltyRate=0;penaltyValue=0;discountMode='percentage';discountRate=3.5;discountValue=0} | Out-Null
    $planState=Request '/companies/1/state'
    Check (@($planState.plans | Where-Object name -eq 'AJUSTE TESTE').Count -eq 2) 'Plano de contas separado entre pagar e receber'
    Request '/companies/1/titles' @{kind='receivable';description='CARTÃO COM TAXA';amount=10000;due=$today;category='AJUSTE TESTE';notes='';installments=1} | Out-Null
    $cardTitle=@((Request '/companies/1/state').titles | Where-Object description -eq 'CARTÃO COM TAXA')[0]
    Check ($cardTitle.discountMode -eq 'percentage' -and $cardTitle.discountRate -eq 3.5) 'Taxa percentual do plano copiada para o recebimento'
    $beforeCard=@((Request '/companies/1/state').accounts | Where-Object id -eq 1)[0].balance
    Request "/companies/1/titles/$($cardTitle.id)/settle" @{accountId=1;principal=10000;interest=0;penalty=0;discount=350;additional=0;date=$today} | Out-Null
    $afterCard=@((Request '/companies/1/state').accounts | Where-Object id -eq 1)[0].balance
    Check (($afterCard-$beforeCard) -eq 9650) 'Taxa da administradora reduz o valor recebido'
    Request '/companies/1/titles' @{kind='payable';description='JUROS FIXOS';amount=10000;due=$today;category='AJUSTE TESTE';notes='';installments=1} | Out-Null
    $fixedTitle=@((Request '/companies/1/state').titles | Where-Object description -eq 'JUROS FIXOS')[0]
    Check ($fixedTitle.interestMode -eq 'value' -and $fixedTitle.interestValue -eq 275) 'Valor fixo do plano copiado para o pagamento'
    $beforeFixed=@((Request '/companies/1/state').accounts | Where-Object id -eq 1)[0].balance
    Request "/companies/1/titles/$($fixedTitle.id)/settle" @{accountId=1;principal=10000;interest=275;penalty=0;discount=0;additional=0;date=$today} | Out-Null
    $afterFixed=@((Request '/companies/1/state').accounts | Where-Object id -eq 1)[0].balance
    Check (($beforeFixed-$afterFixed) -eq 10275) 'Juros fixos aumentam o pagamento'
    $cardReference = (Get-Date).ToString('yyyy-MM')
    $cardDue = (Get-Date -Day 10).AddMonths(1).ToString('yyyy-MM-dd')
    $card = Request '/companies/1/credit-cards' @{name='Itau empresarial';lastFour='1234';closingDay=31;dueDay=10;accountId=1;notes='Cartao de compras'}
    $purchase = Request "/companies/1/credit-cards/$($card.id)/purchases" @{description='Notebook';amount=30000;purchaseDate=$today;mode='installments';installments=3;category='Equipamentos';notes='Uso administrativo'}
    $cardState = Request '/companies/1/state'
    $invoices = @($cardState.creditCardInvoices | Where-Object cardId -eq $card.id | Sort-Object referenceMonth)
    Check ($invoices.Count -eq 3 -and $invoices[0].referenceMonth -eq $cardReference -and $invoices[0].dueDate -eq $cardDue -and $invoices[0].amount -eq 10000) 'Compra parcelada gera uma fatura por mes com vencimento correto'
    Check ($null -eq $invoices[0].titleId -and @($cardState.titles | Where-Object documentNumber -like "CARTAO-$($card.id)-*").Count -eq 0) 'Fatura aberta ainda não cria contas a pagar'
    $extra = Request "/companies/1/credit-cards/$($card.id)/purchases" @{description='Material';amount=5000;purchaseDate=$today;installments=1;category='Material de limpeza';notes=''}
    $cardState = Request '/companies/1/state'
    $currentInvoice = @($cardState.creditCardInvoices | Where-Object { $_.cardId -eq $card.id -and $_.referenceMonth -eq $cardReference })[0]
    Check ($currentInvoice.amount -eq 15000 -and @($cardState.creditCardPurchases | Where-Object invoiceId -eq $currentInvoice.id).Count -eq 2) 'Compras do mesmo ciclo são somadas na mesma fatura'
    Request "/companies/1/credit-card-purchases/$($extra.purchaseGroup)/delete" @{reason='Exclusao de teste'} | Out-Null
    $cardState = Request '/companies/1/state'
    $currentInvoice = @($cardState.creditCardInvoices | Where-Object { $_.cardId -eq $card.id -and $_.referenceMonth -eq $cardReference })[0]
    Check ($currentInvoice.amount -eq 10000) 'Exclusão da compra atualiza o total da fatura aberta'
    $pastClosingDay = [Math]::Max(1, (Get-Date).Day - 1)
    $afterClosingDate = if ((Get-Date).Day -eq 1) { (Get-Date).AddDays(1).ToString('yyyy-MM-dd') } else { $today }
    $nextReference = (Get-Date -Day 1).AddMonths(1).ToString('yyyy-MM')
    $cycleCard = Request '/companies/1/credit-cards' @{name='Cartao corte';lastFour='4321';closingDay=$pastClosingDay;dueDay=10;accountId=1;notes='Teste de corte'}
    Request "/companies/1/credit-cards/$($cycleCard.id)/purchases" @{description='Compra depois do corte';amount=2500;purchaseDate=$afterClosingDate;installments=1;category='Equipamentos';notes=''} | Out-Null
    $cycleState = Request '/companies/1/state'
    Check (@($cycleState.creditCardInvoices | Where-Object { $_.cardId -eq $cycleCard.id -and $_.referenceMonth -eq $nextReference -and $_.amount -eq 2500 }).Count -eq 1) 'Compra após o fechamento entra na fatura do mês seguinte'
    $recurringStart = $today
    $recurring = Request "/companies/1/credit-cards/$($card.id)/purchases" @{description='Software mensal';amount=9900;purchaseDate=$recurringStart;mode='recurring';installments=1;category='Tecnologia';notes='Sem quantidade fixa de parcelas'}
    $cardState = Request '/companies/1/state'
    $recurringRows = @($cardState.creditCardPurchases | Where-Object recurringChargeId -eq $recurring.recurrenceId | Sort-Object referenceMonth)
    Check ($recurring.generated -eq 2 -and $recurringRows.Count -eq 2 -and @($recurringRows | Where-Object amount -ne 9900).Count -eq 0) 'Cobranca recorrente ocupa somente a fatura atual e a proxima'
    $farRecurring = @($cardState.creditCardPurchases | Where-Object { $null -ne $_.recurringChargeId -and $_.referenceMonth -gt $recurringRows[-1].referenceMonth })
    Check ($farRecurring.Count -eq 0) 'Faturas futuras distantes existem somente quando ha parcelas reais'
    $removedOccurrence = $recurringRows[-1]
    Request "/companies/1/credit-card-purchases/$($removedOccurrence.purchaseGroup)/delete" @{reason='Nao cobrar neste mes'} | Out-Null
    Request '/companies/1/credit-card-recurrences/refresh' @{} | Out-Null
    $cardState = Request '/companies/1/state'
    Check (@($cardState.creditCardPurchases | Where-Object { $_.recurringChargeId -eq $recurring.recurrenceId -and $_.referenceMonth -eq $removedOccurrence.referenceMonth }).Count -eq 0) 'Exclusao de uma ocorrencia recorrente nao recria o mes ignorado'
    Request "/companies/1/credit-card-recurrences/$($recurring.recurrenceId)/stop" @{reason='Fim da assinatura'} | Out-Null
    $cardState = Request '/companies/1/state'
    $stopped = @($cardState.creditCardRecurringCharges | Where-Object id -eq $recurring.recurrenceId)[0]
    Check ($stopped.active -eq 0 -and @($cardState.creditCardPurchases | Where-Object recurringChargeId -eq $recurring.recurrenceId).Count -eq 0) 'Encerramento remove previsoes futuras e preserva o historico fechado'
    Request "/companies/1/credit-cards/$($card.id)/edit" @{name='Itau compras';lastFour='9876';closingDay=30;dueDay=18;accountId=1;notes='Cartao editado'} | Out-Null
    $cardState = Request '/companies/1/state'
    $editedCard = @($cardState.creditCards | Where-Object id -eq $card.id)[0]
    Check ($editedCard.name -eq 'ITAU COMPRAS' -and $editedCard.lastFour -eq '9876' -and $editedCard.closingDay -eq 30 -and $editedCard.dueDay -eq 18) 'Nome e final do cartao podem ser editados'
    Check (@($cardState.creditCardInvoices | Where-Object cardId -eq $card.id).Count -gt 0) 'Edição do cartão preserva suas faturas'
    $backup = Request '/backup' @{}
    Check (Test-Path -LiteralPath $backup.path) 'Backup SQLite criado'
    $before=Request '/companies/1/state'
    Stop-Process -Id $process.Id
    $env:FINANCE_DB=$backup.path
    $process=Start-Process -FilePath 'C:\Tools\dotnet\dotnet.exe' -ArgumentList @($ApiDll) -WorkingDirectory (Join-Path $root 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testDir 'restore.log') -RedirectStandardError (Join-Path $testDir 'restore-error.log')
    for ($i=0; $i -lt 40; $i++) { try { Request '/health' | Out-Null; break } catch { Start-Sleep -Milliseconds 300 } }
    $after=Request '/companies/1/state'
    Check ($after.titles.Count -eq $before.titles.Count -and ($after.accounts.balance | Measure-Object -Sum).Sum -eq ($before.accounts.balance | Measure-Object -Sum).Sum) 'Backup reaberto preserva títulos e saldos'
    Write-Output 'TODOS OS TESTES DE API PASSARAM'
} finally { if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id }; Remove-Item Env:\FINANCE_DB -ErrorAction SilentlyContinue; Remove-Item Env:\FINANCE_URL -ErrorAction SilentlyContinue; Remove-Item Env:\FINANCE_AUTH_DISABLED -ErrorAction SilentlyContinue; Remove-Item Env:\MONALISA_LICENSE_DISABLED -ErrorAction SilentlyContinue }
