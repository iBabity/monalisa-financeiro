let token=sessionStorage.getItem('licenseAdmin')||'';
let licenseRows=[];
let updateRows=[];
let refreshTimer=null;
let loading=false;
const api=async(path,data)=>{const r=await fetch('/api'+path,{method:data?'POST':'GET',headers:{...(data?{'Content-Type':'application/json'}:{}),...(token?{Authorization:'Bearer '+token}:{})},body:data?JSON.stringify(data):undefined});const b=await r.json();if(!r.ok)throw new Error(b.error||'Operação recusada.');return b};

async function login(){try{const r=await api('/admin/login',{username:user.value,pin:pin.value});token=r.token;sessionStorage.setItem('licenseAdmin',token);showPanel()}catch(e){loginError.textContent=e.message}}
function logout(){if(refreshTimer)clearInterval(refreshTimer);refreshTimer=null;sessionStorage.removeItem('licenseAdmin');token='';panel.hidden=true;document.querySelector('#login').hidden=false}
async function showPanel(){document.querySelector('#login').hidden=true;panel.hidden=false;await load();if(refreshTimer)clearInterval(refreshTimer);refreshTimer=setInterval(autoRefresh,10000)}

function autoRefresh(){
  if(document.hidden||loading||document.querySelector('dialog[open]'))return;
  if(['INPUT','SELECT','TEXTAREA'].includes(document.activeElement?.tagName))return;
  load(true);
}

async function load(silent=false){
  if(loading)return;
  loading=true;
  try{
    [licenseRows,updateRows]=await Promise.all([api('/admin/licenses'),api('/admin/updates')]);
    renderUpdates();
    renderLicenses();
    if(window.lastRefresh)lastRefresh.textContent=`ATUALIZADO ÀS ${new Date().toLocaleTimeString('pt-BR')}`;
  }catch(e){if(e.message.includes('novamente'))logout();else if(!silent)alert(e.message)}finally{loading=false}
}

function renderUpdates(){
  latestVersion.textContent=updateRows.length?`VERSÃO ATUAL: ${updateRows[0].version}`:'NENHUMA VERSÃO PUBLICADA';
  updateHistory.innerHTML=updateRows.slice(0,5).map((item,index)=>`<div class="updateRow"><strong>${esc(item.version)}</strong><span>${index===0?'OBRIGATÓRIA ATUAL':'VERSÃO ANTERIOR'}</span><span>${formatBytes(item.size)}</span><span>${esc(formatDate(item.published))}</span><small>${esc(item.notes||'SEM DESCRIÇÃO')}</small></div>`).join('')||'<p>ENVIE O PRIMEIRO INSTALADOR PARA COMEÇAR O CONTROLE DE VERSÕES.</p>';
}

async function publishUpdate(event){
  event.preventDefault();const file=updateFile.files[0];if(!file)return;
  if(!confirm(`PUBLICAR A VERSÃO ${updateVersion.value} COMO ATUALIZAÇÃO OBRIGATÓRIA?`))return;
  const data=new FormData();data.append('version',updateVersion.value);data.append('notes',updateNotes.value);data.append('file',file,file.name);
  publishButton.disabled=true;publishButton.textContent='ENVIANDO ATUALIZAÇÃO...';updateError.textContent='';
  try{const response=await fetch('/api/admin/updates',{method:'POST',headers:{Authorization:'Bearer '+token},body:data});const body=await response.json();if(!response.ok)throw new Error(body.error||'NÃO FOI POSSÍVEL PUBLICAR.');updateForm.reset();await load();alert(`VERSÃO ${body.version} PUBLICADA. AS MÁQUINAS DEVERÃO ATUALIZAR AO ABRIR O FINANCEIRO.`)}catch(error){updateError.textContent=error.message}finally{publishButton.disabled=false;publishButton.textContent='PUBLICAR ATUALIZAÇÃO'}
}

function renderLicenses(){
  const term=String(searchBox?.value||'').trim().toUpperCase();
  const digits=term.replace(/\D/g,'');
  const filtered=licenseRows.filter(row=>!term||[row.name,row.document,formatDocument(row.document),row.machineName,row.status].some(value=>String(value||'').toUpperCase().includes(term))||(digits&&String(row.document||'').includes(digits)));
  const groups=new Map();
  for(const row of filtered){const key=`${row.holderType}:${row.document}`;if(!groups.has(key))groups.set(key,[]);groups.get(key).push(row)}
  licenses.innerHTML=[...groups.values()].map(renderHolder).join('')||(term?'<p>NENHUM RESULTADO PARA ESTA PESQUISA.</p>':'<p>NENHUMA PESSOA OU EMPRESA CADASTRADA.</p>');
}

function renderHolder(rows){
  const first=rows[0];
  return `<section class="holder"><div class="holderHead"><div><span class="holderType">${first.holderType==='person'?'PESSOA':'EMPRESA'}</span><h2>${esc(first.name)}</h2><p>${first.holderType==='person'?'CPF':'CNPJ'}: ${esc(formatDocument(first.document))}</p></div><button class="secondary" onclick="openMachine(${first.id})">+ ADICIONAR MÁQUINA</button></div><div class="machineGrid">${rows.map(renderMachine).join('')}</div></section>`;
}

function renderMachine(x){
  const blocked=x.status==='blocked';
  const linked=x.linked===true||x.inUse===true;
  return `<article class="license ${x.status}"><div class="cardTitle"><div><span class="machineLabel">MÁQUINA</span><h3>${esc(x.machineName)}</h3></div><span class="usage ${linked?'linked':'free'}">${linked?'EM USO':'LIVRE'}</span></div><div class="meta">IDENTIFICAÇÃO: ${esc(x.installationId||'AINDA NÃO ATIVADA')}<br>VERSÃO INSTALADA: ${esc(x.appVersion||'AINDA NÃO INFORMADA')}<br>ÚLTIMO CONTATO: ${esc(formatDate(x.lastSeen))}</div><div class="keyRow"><code>${esc(x.activationKey||'CHAVE ANTIGA NÃO DISPONÍVEL')}</code>${x.activationKey?`<button class="secondary compact" onclick="copyText('${escAttr(x.activationKey)}',this)">COPIAR</button>`:!linked?`<button class="secondary compact" onclick="regenerateKey(${x.id})">GERAR CHAVE</button>`:''}</div><select id="s${x.id}">${[['active','ATIVA'],['warning','AVISO'],['read_only','SOMENTE CONSULTA'],['blocked','BLOQUEADA']].map(s=>`<option value="${s[0]}" ${x.status===s[0]?'selected':''}>${s[1]}</option>`).join('')}</select><input id="m${x.id}" value="${esc(x.message||'')}" placeholder="MENSAGEM PARA ESTA MÁQUINA"><div class="actions wrap"><button class="secondary" onclick="resetMachine(${x.id})" ${linked?'':'disabled'}>DESVINCULAR MÁQUINA</button><button class="${blocked?'release':'danger'}" onclick="toggleMachine(${x.id},${blocked})">${blocked?'LIBERAR MÁQUINA':'BLOQUEAR MÁQUINA'}</button><button onclick="statusChange(${x.id})">SALVAR SITUAÇÃO</button></div></article>`;
}

function openCreate(){createTitle.textContent='NOVA PESSOA OU EMPRESA';holderType.disabled=false;holderName.readOnly=false;holderDocument.readOnly=false;holderType.value='company';holderName.value='';holderDocument.value='';machineName.value='';notes.value='';createError.textContent='';updateDocumentField();create.showModal()}
function openMachine(id){const row=licenseRows.find(x=>x.id===id);if(!row)return;createTitle.textContent='ADICIONAR MÁQUINA';holderType.value=row.holderType;holderType.disabled=true;holderName.value=row.name;holderName.readOnly=true;holderDocument.value=formatDocument(row.document);holderDocument.readOnly=true;machineName.value='';notes.value='';createError.textContent='';updateDocumentField();create.showModal()}
function updateDocumentField(){const person=holderType.value==='person';documentLabel.firstChild.textContent=person?'CPF':'CNPJ';holderDocument.placeholder=person?'000.000.000-00':'00.000.000/0000-00';holderDocument.maxLength=person?14:18;holderDocument.value=formatDocument(holderDocument.value,person?'person':'company')}
async function createLicense(e){e.preventDefault();try{const r=await api('/admin/licenses',{name:holderName.value,document:holderDocument.value,holderType:holderType.value,machineName:machineName.value,notes:notes.value});create.close();createdKey.textContent=r.activationKey;keyDialog.showModal();await load()}catch(e){createError.textContent=e.message}}
async function statusChange(id){try{await api(`/admin/licenses/${id}/status`,{status:document.querySelector('#s'+id).value,message:document.querySelector('#m'+id).value});await load()}catch(e){alert(e.message);await load()}}
async function toggleMachine(id,isBlocked){const action=isBlocked?'LIBERAR':'BLOQUEAR';if(!confirm(`${action} O ACESSO DESTA MÁQUINA?`))return;try{await api(`/admin/licenses/${id}/status`,{status:isBlocked?'active':'blocked',message:document.querySelector('#m'+id).value});await load()}catch(e){alert(e.message)}}
async function resetMachine(id){if(!confirm('DESVINCULAR ESTA MÁQUINA E DEIXAR A CHAVE LIVRE PARA OUTRA INSTALAÇÃO? O BANCO FINANCEIRO NÃO SERÁ EXCLUÍDO.'))return;try{await api(`/admin/licenses/${id}/reset-installation`,{});await load()}catch(e){alert(e.message)}}
async function regenerateKey(id){if(!confirm('GERAR UMA NOVA CHAVE PARA ESTA LICENÇA LIVRE?'))return;try{const r=await api(`/admin/licenses/${id}/regenerate-key`,{});createdKey.textContent=r.activationKey;keyDialog.showModal();await load()}catch(e){alert(e.message)}}

const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const escAttr=s=>String(s??'').replace(/['\\]/g,c=>c==='\\'?'\\\\':"\\'");
function formatDocument(value,type){const raw=String(value??'').replace(/\D/g,'');const person=type==='person'||(!type&&raw.length<=11);const d=raw.slice(0,person?11:14);if(person)return d.replace(/^(\d{3})(\d)/,'$1.$2').replace(/^(\d{3})\.(\d{3})(\d)/,'$1.$2.$3').replace(/(\d{3})(\d{1,2})$/,'$1-$2');return d.replace(/^(\d{2})(\d)/,'$1.$2').replace(/^(\d{2})\.(\d{3})(\d)/,'$1.$2.$3').replace(/\.(\d{3})(\d)/,'.$1/$2').replace(/(\d{4})(\d)/,'$1-$2')}
function formatDate(value){if(!value)return 'NUNCA';const d=new Date(value);return Number.isNaN(d.getTime())?value:d.toLocaleString('pt-BR')}
function formatBytes(value){const bytes=Number(value||0);return bytes>=1048576?`${(bytes/1048576).toFixed(1).replace('.',',')} MB`:`${Math.round(bytes/1024)} KB`}
async function copyText(value,button){try{await navigator.clipboard.writeText(value);const old=button.textContent;button.textContent='COPIADA';setTimeout(()=>button.textContent=old,1600)}catch{alert('NÃO FOI POSSÍVEL COPIAR A CHAVE.')}}
holderDocument.addEventListener('input',()=>{holderDocument.value=formatDocument(holderDocument.value,holderType.value)});
if(token)showPanel();
