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

let chatHistory = [];
let isLoading = false;

// 默认自动滚动到底部
let autoScroll = true;

// 当用户滚动时，判断用户是否离开底部区域
output.addEventListener('scroll', () => {
    const distanceToBottom = output.scrollHeight - output.scrollTop - output.clientHeight;
    autoScroll = distanceToBottom < 10;
});

// 图片预览模态框
document.body.insertAdjacentHTML('beforeend', '<div class="image-modal"><img src="" alt="Preview"></div>');
const imageModal = document.querySelector('.image-modal');

document.addEventListener('DOMContentLoaded', function() {
    const clearBtn = document.getElementById('clearBtn');
    const chatHeader = document.querySelector('.chat-header');
    const chatControls = document.querySelector('.chat-controls');
    
    function moveButton() {
        if (window.innerWidth <= 768) {
            chatHeader.appendChild(clearBtn);
        } else {
            chatControls.appendChild(clearBtn);
        }
    }
    
    // 初始化时执行一次
    moveButton();
    
    // 监听窗口大小变化
    window.addEventListener('resize', moveButton);
});

async function sendMessage() {
    const prompt = promptInput.value.trim();
    const model = modelSelect.value;

    if (!prompt && imageInput.files.length === 0) return;

    setLoading(true);

    // 用户输入消息
    if (prompt) {
        addMessage(prompt, 'user');
        chatHistory.push({ role: "user", Content: prompt });
    }

    // 处理图片文件
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
                        // 创建AI消息气泡（初次收到时）
                        aiBubble = addMessage('', 'ai');
                    }
                    renderMarkdown(aiBubble, aiMessage);
                    // 更新右下角复制按钮事件
                    updateBubbleCopyButton(aiBubble.closest('.message'));

                    if (autoScroll) {
                        output.scrollTop = output.scrollHeight;
                    }
                }
            }
        }

        if (aiMessage) {
            chatHistory.push({ role: "model", Content: aiMessage });
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
    // 文件名和类型换行显示
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
    bubble.style.position = 'relative';

    // 底部右下角复制按钮
    const copyBtn = document.createElement('button');
    copyBtn.classList.add('copy-button');
    copyBtn.textContent = 'Copy';
    // 对于用户消息立即可用，AI消息在render后会重新绑定
    copyBtn.onclick = () => {
        const messageContent = bubble.querySelector('.message-content').innerText;
        handleCopyButton(copyBtn, messageContent);
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

// Enter发送（Shift+Enter换行）
promptInput.addEventListener('keypress', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !isLoading) {
        e.preventDefault();
        sendMessage();
    }
});

clearBtn.addEventListener('click', () => {
    output.innerHTML = '';
    chatHistory = [];
});

function renderMarkdown(element, content) {
    element.innerHTML = marked.parse(content);

    // 给所有code元素加上hljs类
    element.querySelectorAll('pre code').forEach(codeEl => {
        codeEl.classList.add('hljs');
    });

    // 为所有代码块添加头部和复制按钮
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

        // 代码块复制功能
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

    // 渲染数学公式
    renderMathInElement(element, {
        delimiters: [
            {left: '$$', right: '$$', display: true},
            {left: '$', right: '$', display: false}
        ]
    });
}

function updateBubbleCopyButton(msgDiv) {
    const bubble = msgDiv.querySelector('.bubble');
    const copyBtn = bubble.querySelector('.copy-button');
    if (copyBtn) {
        copyBtn.onclick = () => {
            const markdownContent = bubble.querySelector('.markdown-content');
            const messageContent = markdownContent ? markdownContent.innerText : bubble.querySelector('.message-content').innerText;
            handleCopyButton(copyBtn, messageContent);
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

// 输入框高度自适应(有最大高度，超出出现滚动条)
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