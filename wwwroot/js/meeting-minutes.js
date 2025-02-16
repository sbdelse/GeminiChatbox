document.addEventListener('DOMContentLoaded', function() {
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

    let isSubmitting = false;
    let processingFiles = new Set();

    const uploadForm = document.getElementById('uploadForm');
    const fileInput = document.getElementById('audioFile');
    const startTime = document.getElementById('startTime');
    const endTime = document.getElementById('endTime');
    const submitBtn = document.getElementById('submitBtn');
    const spinner = document.getElementById('spinner');
    const status = document.getElementById('status');
    const transcription = document.getElementById('transcription');
    const analysis = document.getElementById('analysis');

    // 隐藏初始结果区域
    document.getElementById('result').style.display = 'none';

    let eventSource = null;

    uploadForm.addEventListener('submit', async (e) => {
        e.preventDefault();
        console.log('Form submission started');

        // 防止重复提交
        if (isSubmitting) {
            console.log('Submission in progress, skipping...');
            return;
        }

        if (!fileInput.files[0]) {
            showAlert('请选择文件', 'warning');
            return;
        }

        isSubmitting = true;
        setLoading(true);
        document.getElementById('result').style.display = 'block';
        clearResults();

        // 关闭现有的 EventSource
        if (eventSource) {
            eventSource.close();
            eventSource = null;
        }

        const fileName = fileInput.files[0].name + '-' + Date.now();
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 30000);

        try {
            const formData = new FormData();
            formData.append('file', fileInput.files[0]);

            const uploadResponse = await fetch('/MeetingMinutes/Upload', {
                method: 'POST',
                body: formData,
                signal: controller.signal
            });
            clearTimeout(timeoutId);

            const responseData = await uploadResponse.json();

            if (!uploadResponse.ok) {
                throw new Error(responseData.message || '文件上传失败');
            }

            eventSource = new EventSource(`/MeetingMinutes/Process?${new URLSearchParams({
                fileName: responseData.fileName,
                startTime: startTime.value,
                endTime: endTime.value
            })}`);

            eventSource.onmessage = handleServerMessage;
            eventSource.onerror = handleServerError;
            eventSource.onopen = () => console.log('SSE connection opened');

        } catch (error) {
            handleError(error);
        } finally {
            isSubmitting = false;
        }
    });

    function handleServerMessage(event) {
        console.log('Received message:', event.data);
        
        try {
            const data = JSON.parse(event.data);
            
            switch (data.type) {
                case 'status':
                    updateStatus(data.content);
                    break;
                case 'transcription':
                    updateTranscription(data.content);
                    break;
                case 'analysis':
                    updateAnalysis(data.content);
                    break;
                case 'done':
                    if (eventSource) {
                        eventSource.close();
                    }
                    setLoading(false);
                    break;
                case 'error':
                    showAlert('错误: ' + data.content, 'danger');
                    if (eventSource) {
                        eventSource.close();
                    }
                    setLoading(false);
                    break;
            }
        } catch (error) {
            console.error('Error parsing message:', error);
        }
    }

    function handleServerError(error) {
        console.error('SSE Error:', error);
        if (error.target.readyState === EventSource.CLOSED) {
            setLoading(false);
        }
    }

    function handleError(error) {
        console.error('Error:', error);
        showAlert('错误: ' + error.message, 'danger');
        setLoading(false);
    }

    function setLoading(loading) {
        submitBtn.disabled = loading;
        spinner.classList.toggle('d-none', !loading);
    }

    function clearResults() {
        status.innerHTML = '';
        transcription.innerHTML = '';
        analysis.innerHTML = '';
    }

    function updateStatus(message) {
        status.textContent = message;
    }

    function updateTranscription(content) {
        transcription.innerHTML += content.split('\n').join('<br>');
    }

    function updateAnalysis(content) {
        analysis.innerHTML = marked.parse(content);
        
        if (typeof renderMathInElement === 'function') {
            renderMathInElement(analysis, {
                delimiters: [
                    {left: '$$', right: '$$', display: true},
                    {left: '$', right: '$', display: false}
                ]
            });
        }
        
        analysis.querySelectorAll('pre code').forEach(block => {
            hljs.highlightElement(block);
        });
    }

    function showAlert(message, type) {
        status.className = `alert alert-${type} mb-3`;
        status.textContent = message;
    }
}); 