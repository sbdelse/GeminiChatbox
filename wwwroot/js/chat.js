document.addEventListener('DOMContentLoaded', function() {
    if (typeof renderMathInElement === 'undefined') {
        window.addEventListener('load', function() {
            document.querySelectorAll('.message-content').forEach(element => {
                if (typeof renderMathInElement === 'function') {
                    renderMathInElement(element, {
                        delimiters: [
                            {left: '$$', right: '$$', display: true},
                            {left: '$', right: '$', display: false}
                        ]
                    });
                }
            });
        });
    }
});

marked.setOptions({
    highlight: function (code, lang) {
        if (lang && hljs.getLanguage(lang)) {
            return hljs.highlight(code, {language: lang}).value;
        } else {
            return hljs.highlightAuto(code).value;
        }
    },
    gfm: true,
    breaks: true
});

const sendBtn = document.getElementById('sendBtn');
const promptInput = document.getElementById('promptInput');
const output = document.getElementById('output');
const modelSelect = document.getElementById('modelSelect');
const imageInput = document.getElementById('imageInput');
const clearBtn = document.getElementById('clearBtn');
const pasteBtn = document.getElementById('pasteBtn');
const clearFilesBtn = document.getElementById('clearFilesBtn');
const exportBtn = document.getElementById('exportBtn');
const chatSidebar = document.getElementById('chatSidebar');
const sidebarToggle = document.getElementById('sidebarToggle');
const sidebarOverlay = document.getElementById('sidebarOverlay');
const chatIndexList = document.getElementById('chatIndexList');
const cameraBtn = document.getElementById('cameraBtn');

let chatHistory = [];
let isLoading = false;
let autoScroll = true;
let messageModels = [];

output.addEventListener('scroll', () => {
    const distanceToBottom = output.scrollHeight - output.scrollTop - output.clientHeight;
    autoScroll = distanceToBottom < 10;
});

document.body.insertAdjacentHTML('beforeend', '<div class="image-modal"><img src="" alt="Preview"></div>');
const imageModal = document.querySelector('.image-modal');

document.addEventListener('DOMContentLoaded', function() {
    const clearBtn = document.getElementById('clearBtn');
    const chatHeader = document.querySelector('.chat-header');
    const chatControls = document.querySelector('.chat-controls');
    
    if (clearBtn && chatHeader && chatControls) {
        function moveButton() {
            if (window.innerWidth <= 768) {
                chatHeader.appendChild(clearBtn);
            } else {
                chatControls.appendChild(clearBtn);
            }
        }
        
        moveButton();
        window.addEventListener('resize', moveButton);
    }
});

document.addEventListener('DOMContentLoaded', function() {
    const lastSelectedModel = localStorage.getItem('selectedModel');
    
    if (lastSelectedModel) {
        const modelExists = Array.from(modelSelect.options).some(option => option.value === lastSelectedModel);
        
        if (modelExists) {
            modelSelect.value = lastSelectedModel;
        } else {
            localStorage.removeItem('selectedModel');
        }
    }
});

modelSelect.addEventListener('change', function() {
    localStorage.setItem('selectedModel', this.value);
});

async function sendMessage() {
    const prompt = promptInput.value.trim();
    const model = modelSelect.value;

    if (!prompt && imageInput.files.length === 0) return;

    setLoading(true);

    if (prompt) {
        addMessage(prompt, 'user');
        chatHistory.push({ role: "user", Content: prompt });
    }

    const imagesData = [];
    if (imageInput.files.length > 0) {
        for (const file of imageInput.files) {
            const base64 = await fileToBase64(file);
            imagesData.push({
                MimeType: file.type,
                Data: base64
            });
        }
    }

    promptInput.value = '';
    promptInput.style.height = 'auto';
    imageInput.value = '';
    let aiMessage = '';

    try {
        const response = await fetch('/stream', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                prompt: prompt,
                model: model,
                history: chatHistory.map(h => ({ Role: h.role ?? h.Role, Content: h.Content })),
                images: imagesData
            })
        });

        const reader = response.body.getReader();
        const decoder = new TextDecoder();

        let aiBubble = null;

        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            const chunk = decoder.decode(value);
            const lines = chunk.split('\n');

            for (const line of lines) {
                const trimmed = line.trim();
                if (!trimmed) continue;
                if (trimmed.startsWith('data: ')) {
                    const data = trimmed.slice(6);
                    let textChunk;
                    try {
                        textChunk = JSON.parse(data);
                    } catch (err) {
                        console.error('JSON parse error:', err, 'data:', data);
                        continue;
                    }
                    aiMessage += textChunk;
                    if (!aiBubble) {
                        aiBubble = addMessage('', 'ai');
                    }
                    renderMarkdown(aiBubble, aiMessage);
                    updateBubbleCopyButton(aiBubble.closest('.message'));

                    if (autoScroll) {
                        output.scrollTop = output.scrollHeight;
                    }
                }
            }
        }

        if (aiMessage) {
            chatHistory.push({ role: "model", Content: aiMessage });
            messageModels.push(modelSelect.options[modelSelect.selectedIndex].text);
            updateChatIndex(aiMessage, 'ai');
        }
    } catch (error) {
        console.error('Error:', error);
    }

    setLoading(false);
}

function createFilePreview(file) {
    const filePreview = document.createElement('div');
    filePreview.classList.add('file-preview');

    if (file.type.startsWith('image/')) {
        const img = document.createElement('img');
        img.src = URL.createObjectURL(file);
        img.classList.add('preview-image');
        img.onclick = () => showImagePreview(img.src);
        filePreview.appendChild(img);
    }

    const fileInfo = document.createElement('div');
    fileInfo.classList.add('file-info');
    fileInfo.innerHTML = `
        <span class="file-name">${file.name}</span><br>
        <span class="file-type">${file.type || '未知类型'}</span>
    `;
    filePreview.appendChild(fileInfo);

    return filePreview;
}

function showImagePreview(src) {
    const modalImg = imageModal.querySelector('img');
    modalImg.src = src;
    imageModal.classList.add('active');
}

imageModal.onclick = () => {
    imageModal.classList.remove('active');
};

function addMessage(content, type) {
    const msgDiv = document.createElement('div');
    msgDiv.classList.add('message', type);

    const bubble = document.createElement('div');
    bubble.classList.add('bubble');
    
    // Add special styling for system messages and errors
    if (content.startsWith('[System]')) {
        bubble.classList.add('system-message');
        type = 'system';
    } else if (content.startsWith('[Error]')) {
        bubble.classList.add('error-message');
        type = 'error';
    }
    
    bubble.style.position = 'relative';
    
    bubble.dataset.originalContent = content;

    const copyBtn = document.createElement('button');
    copyBtn.classList.add('copy-button');
    copyBtn.textContent = 'Copy';
    copyBtn.onclick = () => {
        const originalContent = bubble.dataset.originalContent;
        handleCopyButton(copyBtn, originalContent);
    };
    bubble.appendChild(copyBtn);

    if (type === 'user' && imageInput.files.length > 0) {
        const filesPreview = document.createElement('div');
        filesPreview.classList.add('files-preview');

        Array.from(imageInput.files).forEach(file => {
            const filePreview = createFilePreview(file);
            filesPreview.appendChild(filePreview);
        });

        bubble.appendChild(filesPreview);
    }

    const contentDiv = document.createElement('div');
    contentDiv.classList.add('message-content');
    if (type === 'user') {
        contentDiv.textContent = content;
    }
    bubble.appendChild(contentDiv);
    msgDiv.appendChild(bubble);
    output.appendChild(msgDiv);

    if (autoScroll) {
        output.scrollTop = output.scrollHeight;
    }

    if (content) {
        updateChatIndex(content, type);
    }

    return contentDiv;
}

function setLoading(loading) {
    isLoading = loading;
    sendBtn.disabled = loading;
    const spinner = sendBtn.querySelector('.loading-spinner');
    spinner.style.display = loading ? 'inline-block' : 'none';
}

function fileToBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => {
            const base64Str = reader.result.split(',')[1];
            resolve(base64Str);
        };
        reader.onerror = reject;
        reader.readAsDataURL(file);
    });
}

sendBtn.addEventListener('click', sendMessage);

promptInput.addEventListener('keypress', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !isLoading) {
        e.preventDefault();
        sendMessage();
    }
});

clearBtn.addEventListener('click', () => {
    output.innerHTML = '';
    chatHistory = [];
    chatIndexList.innerHTML = '';
});

function renderMarkdown(element, content) {
    element.closest('.bubble').dataset.originalContent = content;
    element.innerHTML = marked.parse(content);

    element.querySelectorAll('pre code').forEach(codeEl => {
        codeEl.classList.add('hljs');
    });

    element.querySelectorAll('pre > code').forEach(codeElement => {
        const pre = codeElement.parentNode;
        const codeBlock = document.createElement('div');
        codeBlock.classList.add('code-block');

        const codeBlockHeader = document.createElement('div');
        codeBlockHeader.classList.add('code-block-header');

        let lang = (codeElement.className.match(/language-(\S+)/) || [])[1] || 'text';
        const langSpan = document.createElement('span');
        langSpan.classList.add('code-language');
        langSpan.textContent = lang;
        codeBlockHeader.appendChild(langSpan);

        const codeCopyBtn = document.createElement('button');
        codeCopyBtn.classList.add('code-copy-button');
        codeCopyBtn.textContent = 'Copy';
        codeBlockHeader.appendChild(codeCopyBtn);

        codeBlock.appendChild(codeBlockHeader);
        codeBlock.appendChild(pre.cloneNode(true));
        pre.parentNode.replaceChild(codeBlock, pre);

        codeCopyBtn.addEventListener('click', () => {
            const text = codeBlock.querySelector('code').textContent;
            navigator.clipboard.writeText(text).then(() => {
                codeCopyBtn.textContent = 'Copied!';
                codeCopyBtn.classList.add('copied');
                setTimeout(() => {
                    codeCopyBtn.textContent = 'Copy';
                    codeCopyBtn.classList.remove('copied');
                }, 2000);
            }).catch(err => {
                console.error('Failed to copy:', err);
            });
        });
    });

    if (typeof renderMathInElement === 'function') {
        renderMathInElement(element, {
            delimiters: [
                {left: '$$', right: '$$', display: true},
                {left: '$', right: '$', display: false}
            ]
        });
    }
}

function updateBubbleCopyButton(msgDiv) {
    const bubble = msgDiv.querySelector('.bubble');
    const copyBtn = bubble.querySelector('.copy-button');
    if (copyBtn) {
        copyBtn.onclick = () => {
            const originalContent = bubble.dataset.originalContent;
            handleCopyButton(copyBtn, originalContent);
        };
    }
}

async function handleCopyButton(button, text) {
    try {
        await navigator.clipboard.writeText(text);
        const originalText = button.textContent;
        button.textContent = 'Copied!';
        button.classList.add('copied');
        setTimeout(() => {
            button.textContent = originalText;
            button.classList.remove('copied');
        }, 2000);
    } catch (err) {
        console.error('Failed to copy:', err);
    }
}

const maxHeight = 200;
promptInput.addEventListener('input', function() {
    this.style.height = 'auto';
    let newHeight = Math.min(this.scrollHeight, maxHeight);
    this.style.height = newHeight + 'px';
    if (this.scrollHeight > maxHeight) {
        this.style.overflowY = 'auto';
    } else {
        this.style.overflowY = 'hidden';
    }
});

function handlePaste(event) {
    const items = (event.clipboardData || event.originalEvent.clipboardData).items;
    
    for (const item of items) {
        if (item.type.indexOf('image') === 0) {
            const blob = item.getAsFile();
            const file = new File([blob], `pasted-image-${Date.now()}.png`, { type: blob.type });
            
            const dataTransfer = new DataTransfer();
            
            if (imageInput.files.length > 0) {
                Array.from(imageInput.files).forEach(existingFile => {
                    dataTransfer.items.add(existingFile);
                });
            }
            
            dataTransfer.items.add(file);
            imageInput.files = dataTransfer.files;
            
            const filesPreview = document.querySelector('.files-preview') || document.createElement('div');
            filesPreview.classList.add('files-preview');
            filesPreview.appendChild(createFilePreview(file));
            
            event.preventDefault();
        }
    }
}

document.addEventListener('paste', handlePaste);
pasteBtn.addEventListener('click', () => {
    navigator.clipboard.read().then(async items => {
        for (const item of items) {
            for (const type of item.types) {
                if (type.startsWith('image/')) {
                    const blob = await item.getType(type);
                    const file = new File([blob], `pasted-image-${Date.now()}.png`, { type });
                    
                    const dataTransfer = new DataTransfer();
                    
                    if (imageInput.files.length > 0) {
                        Array.from(imageInput.files).forEach(existingFile => {
                            dataTransfer.items.add(existingFile);
                        });
                    }
                    
                    dataTransfer.items.add(file);
                    imageInput.files = dataTransfer.files;
                }
            }
        }
    }).catch(err => {
        console.error('Failed to read clipboard:', err);
    });
});

clearFilesBtn.addEventListener('click', () => {
    imageInput.value = '';
    const filesPreview = document.querySelector('.files-preview');
    if (filesPreview) {
        filesPreview.remove();
    }
});

exportBtn.addEventListener('click', async () => {
    const messages = Array.from(output.children);
    let markdown = `# Chat History\n\nDate: ${new Date().toLocaleString()}\n\n---\n\n`;
    let modelIndex = 0;
    
    for (let i = 0; i < messages.length; i++) {
        const msg = messages[i];
        const isUser = msg.classList.contains('user');
        const bubble = msg.querySelector('.bubble');
        const originalContent = bubble.dataset.originalContent;
        const filesPreview = bubble.querySelector('.files-preview');
        
        if (isUser) {
            markdown += '**User**:\n';
        } else {
            markdown += `**Assistant** (${messageModels[modelIndex++]}):\n`;
        }
        
        markdown += originalContent + '\n\n';
        
        if (filesPreview) {
            const images = filesPreview.querySelectorAll('.preview-image');
            for (const img of images) {
                try {
                    const response = await fetch(img.src);
                    const blob = await response.blob();
                    const base64 = await new Promise((resolve) => {
                        const reader = new FileReader();
                        reader.onloadend = () => resolve(reader.result);
                        reader.readAsDataURL(blob);
                    });
                    
                    markdown += `![Uploaded Image](${base64})\n`;
                } catch (error) {
                    console.error('Error converting image to base64:', error);
                    markdown += `![Uploaded Image](${img.src})\n`;
                }
            }
            markdown += '\n';
        }
        
        markdown += '---\n\n';
    }
    
    const blob = new Blob([markdown], { type: 'text/markdown' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `chat-history-${new Date().toISOString().slice(0,10)}.md`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
});

function checkSidebarVisibility() {
    if (window.innerWidth > 1200) {
        chatSidebar.classList.add('visible');
        sidebarOverlay.classList.remove('active');
    } else {
        chatSidebar.classList.remove('visible');
    }
}

checkSidebarVisibility();
window.addEventListener('resize', checkSidebarVisibility);

sidebarToggle.addEventListener('click', () => {
    chatSidebar.classList.toggle('active');
    sidebarOverlay.classList.toggle('active');
    document.body.style.overflow = chatSidebar.classList.contains('active') ? 'hidden' : '';
});

sidebarOverlay.addEventListener('click', () => {
    chatSidebar.classList.remove('active');
    sidebarOverlay.classList.remove('active');
    document.body.style.overflow = '';
});

function updateChatIndex(content, type) {
    const li = document.createElement('li');
    li.classList.add('chat-index-item', type);
    
    const icon = type === 'user' ? 'fa-user' : 'fa-robot';
    const truncatedContent = content.length > 30 
        ? content.substring(0, 30) + '...' 
        : content;
    
    li.innerHTML = `
        <div class="content">
            <i class="fas ${icon} fa-sm" style="margin-right: 8px;"></i>
            ${truncatedContent}
        </div>
    `;
    
    li.dataset.fullContent = content;
    
    li.addEventListener('click', () => {
        const messages = output.querySelectorAll('.message');
        for (let msg of messages) {
            const msgContent = msg.querySelector('.message-content').textContent;
            if (msgContent.includes(content)) {
                output.scrollTo({
                    top: msg.offsetTop - output.offsetTop,
                    behavior: 'smooth'
                });
                
                msg.classList.add('highlight');
                setTimeout(() => {
                    msg.classList.remove('highlight');
                }, 2000);
                break;
            }
        }
        
        if (window.innerWidth <= 1200) {
            chatSidebar.classList.remove('active');
            sidebarOverlay.classList.remove('active');
        }
    });
    
    chatIndexList.appendChild(li);
}

const style = document.createElement('style');
style.textContent = `
    .message.highlight {
        animation: highlight 2s ease-in-out;
    }
    
    @keyframes highlight {
        0% { background: transparent; }
        25% { background: rgba(13, 110, 253, 0.1); }
        75% { background: rgba(13, 110, 253, 0.1); }
        100% { background: transparent; }
    }
    
    @media (prefers-color-scheme: dark) {
        @keyframes highlight {
            0% { background: transparent; }
            25% { background: rgba(91, 158, 255, 0.15); }
            75% { background: rgba(91, 158, 255, 0.15); }
            100% { background: transparent; }
        }
    }

    .bubble.system-message {
        background-color: #e8f4f8;
        border-left: 4px solid #0dcaf0;
        color: #055160;
    }

    .bubble.error-message {
        background-color: #f8d7da;
        border-left: 4px solid #dc3545;
        color: #842029;
    }

    @media (prefers-color-scheme: dark) {
        .bubble.system-message {
            background-color: rgba(13, 202, 240, 0.15);
            color: #98c7d0;
        }

        .bubble.error-message {
            background-color: rgba(220, 53, 69, 0.15);
            color: #ea868f;
        }
    }
`;
document.head.appendChild(style);

cameraBtn.addEventListener('click', () => {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        const cameraInput = document.createElement('input');
        cameraInput.type = 'file';
        cameraInput.accept = 'image/*';
        cameraInput.capture = 'environment';
        
        cameraInput.addEventListener('change', handleCameraCapture);
        cameraInput.click();
        return;
    }

    const cameraInput = document.createElement('input');
    cameraInput.type = 'file';
    cameraInput.accept = 'image/*';
    cameraInput.capture = 'environment';
    
    cameraInput.addEventListener('change', handleCameraCapture);
    cameraInput.click();
});

function handleCameraCapture(event) {
    if (event.target.files && event.target.files[0]) {
        const dataTransfer = new DataTransfer();

        if (imageInput.files.length > 0) {
            Array.from(imageInput.files).forEach(existingFile => {
                dataTransfer.items.add(existingFile);
            });
        }

        dataTransfer.items.add(event.target.files[0]);
        imageInput.files = dataTransfer.files;

        const filesPreview = document.querySelector('.files-preview') || document.createElement('div');
        filesPreview.classList.add('files-preview');
        filesPreview.appendChild(createFilePreview(event.target.files[0]));
    }
}