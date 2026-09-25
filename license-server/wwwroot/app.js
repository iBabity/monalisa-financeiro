let token=sessionStorage.getItem('licenseAdmin')||'';
let licenseRows=[];
const api=async(path,data)=>{const r=await fetch('/api'+path,{method:data?'POST':'GET',headers:{...(data?{'Content-Type':'application/json'}:{}),...(token?{Authorization:'Bearer '+token}:{})},body:data?JSON.stringify(data):undefined});const b=await r.json();if(!r.ok)throw new Error(b.error||'Operação recusada.');return b};

async function login(){try{const r=await api('/admin/login',{username:user.value,pin:pin.value});token=r.token;sessionStorage.setItem('licenseAdmin',token);showPanel()}catch(e){loginError.textContent=e.message}}
function logout(){sessionStorage.removeItem('licenseAdmin');token='';panel.hidden=true;document.querySelector('#login').hidden=false}
async function showPanel(){document.querySelector('#login').hidden=true;panel.hidden=false;await load()}

async function load(){
  try{
    licenseRows=await api('/admin/licenses');
    const groups=new Map();
    for(const row of licenseRows){const key=`${row.holderType}:${row.document}`;if(!groups.has(key))groups.set(key,[]);groups.get(key).push(row)}
    licenses.innerHTML=[...groups.values()].map(renderHolder).join('')||'<p>NENHUMA PESSOA OU EMPRESA CADASTRADA.</p>';
  }catch(e){if(e.message.includes('novamente'))logout();else alert(e.message)}
}

function renderHolder(rows){
  const first=rows[0];
  return `<section class="holder"><div class="holderHead"><div><span class="holderType">${first.holderType==='person'?'PESSOA':'EMPRESA'}</span><h2>${esc(first.name)}</h2><p>${first.holderType==='person'?'CPF':'CNPJ'}: ${esc(formatDocument(first.document))}</p></div><button class="secondary" onclick="openMachine(${first.id})">+ ADICIONAR MÁQUINA</button></div><div class="machineGrid">${rows.map(renderMachine).join('')}</div></section>`;
}

function renderMachine(x){
  const blocked=x.status==='blocked';
  return `<article class="license ${x.status}"><div class="cardTitle"><div><span class="machineLabel">MÁQUINA</span><h3>${esc(x.machineName)}</h3></div><span class="usage ${x.inUse?'used':'free'}">${x.inUse?'EM USO':'LIVRE'}</span></div><div class="meta">IDENTIFICAÇÃO: ${esc(x.installationId||'AINDA NÃO ATIVADA')}<br>ÚLTIMO CONTATO: ${esc(formatDate(x.lastSeen))}</div><div class="keyRow"><code>${esc(x.activationKey||'CHAVE ANTIGA NÃO DISPONÍVEL')}</code>${x.activationKey?`<button class="secondary compact" onclick="copyText('${escAttr(x.activationKey)}',this)">COPIAR</button>`:!x.inUse?`<button class="secondary compact" onclick="regenerateKey(${x.id})">GERAR CHAVE</button>`:''}</div><select id="s${x.id}">${[['active','ATIVA'],['warning','AVISO'],['read_only','SOMENTE CONSULTA'],['blocked','BLOQUEADA']].map(s=>`<option value="${s[0]}" ${x.status===s[0]?'selected':''}>${s[1]}</option>`).join('')}</select><input id="m${x.id}" value="${esc(x.message||'')}" placeholder="MENSAGEM PARA ESTA MÁQUINA"><div class="actions wrap"><button class="secondary" onclick="resetMachine(${x.id})" ${x.inUse?'':'disabled'}>TROCAR COMPUTADOR</button><button class="${blocked?'release':'danger'}" onclick="toggleMachine(${x.id},${blocked})">${blocked?'LIBERAR MÁQUINA':'BLOQUEAR MÁQUINA'}</button><button onclick="statusChange(${x.id})">SALVAR SITUAÇÃO</button></div></article>`;
}

function openCreate(){createTitle.textContent='NOVA PESSOA OU EMPRESA';holderType.disabled=false;holderName.readOnly=false;holderDocument.readOnly=false;holderType.value='company';holderName.value='';holderDocument.value='';machineName.value='';notes.value='';createError.textContent='';updateDocumentField();create.showModal()}
function openMachine(id){const row=licenseRows.find(x=>x.id===id);if(!row)return;createTitle.textContent='ADICIONAR MÁQUINA';holderType.value=row.holderType;holderType.disabled=true;holderName.value=row.name;holderName.readOnly=true;holderDocument.value=formatDocument(row.document);holderDocument.readOnly=true;machineName.value='';notes.value='';createError.textContent='';updateDocumentField();create.showModal()}
function updateDocumentField(){const person=holderType.value==='person';documentLabel.firstChild.textContent=person?'CPF':'CNPJ';holderDocument.placeholder=person?'000.000.000-00':'00.000.000/0000-00';holderDocument.maxLength=person?14:18;holderDocument.value=formatDocument(holderDocument.value,person?'person':'company')}
async function createLicense(e){e.preventDefault();try{const r=await api('/admin/licenses',{name:holderName.value,document:holderDocument.value,holderType:holderType.value,machineName:machineName.value,notes:notes.value});create.close();createdKey.textContent=r.activationKey;keyDialog.showModal();await load()}catch(e){createError.textContent=e.message}}
async function statusChange(id){try{await api(`/admin/licenses/${id}/status`,{status:document.querySelector('#s'+id).value,message:document.querySelector('#m'+id).value});await load()}catch(e){alert(e.message);await load()}}
async function toggleMachine(id,isBlocked){const action=isBlocked?'LIBERAR':'BLOQUEAR';if(!confirm(`${action} O ACESSO DESTA MÁQUINA?`))return;try{await api(`/admin/licenses/${id}/status`,{status:isBlocked?'active':'blocked',message:document.querySelector('#m'+id).value});await load()}catch(e){alert(e.message)}}
async function resetMachine(id){if(!confirm('DESVINCULAR O COMPUTADOR ATUAL E DEIXAR ESTA LICENÇA LIVRE PARA OUTRO COMPUTADOR?'))return;try{await api(`/admin/licenses/${id}/reset-installation`,{});await load()}catch(e){alert(e.message)}}
async function regenerateKey(id){if(!confirm('GERAR UMA NOVA CHAVE PARA ESTA LICENÇA LIVRE?'))return;try{const r=await api(`/admin/licenses/${id}/regenerate-key`,{});createdKey.textContent=r.activationKey;keyDialog.showModal();await load()}catch(e){alert(e.message)}}

const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const escAttr=s=>String(s??'').replace(/['\\]/g,c=>c==='\\'?'\\\\':"\\'");
function formatDocument(value,type){const raw=String(value??'').replace(/\D/g,'');const person=type==='person'||(!type&&raw.length<=11);const d=raw.slice(0,person?11:14);if(person)return d.replace(/^(\d{3})(\d)/,'$1.$2').replace(/^(\d{3})\.(\d{3})(\d)/,'$1.$2.$3').replace(/(\d{3})(\d{1,2})$/,'$1-$2');return d.replace(/^(\d{2})(\d)/,'$1.$2').replace(/^(\d{2})\.(\d{3})(\d)/,'$1.$2.$3').replace(/\.(\d{3})(\d)/,'.$1/$2').replace(/(\d{4})(\d)/,'$1-$2')}
function formatDate(value){if(!value)return 'NUNCA';const d=new Date(value);return Number.isNaN(d.getTime())?value:d.toLocaleString('pt-BR')}
async function copyText(value,button){try{await navigator.clipboard.writeText(value);const old=button.textContent;button.textContent='COPIADA';setTimeout(()=>button.textContent=old,1600)}catch{alert('NÃO FOI POSSÍVEL COPIAR A CHAVE.')}}
holderDocument.addEventListener('input',()=>{holderDocument.value=formatDocument(holderDocument.value,holderType.value)});
if(token)showPanel();
