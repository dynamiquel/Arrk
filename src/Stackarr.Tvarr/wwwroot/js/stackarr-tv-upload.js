// Streaming uploads for the TV local-import dialog. The file is sent as the raw request body so a
// multi-gigabyte remux never has to pass through Blazor's SignalR circuit or a multipart temp copy.
window.stackarrTvUpload = {
    names: function (input) {
        return Array.from(input.files || []).map(function (file) {
            return { name: file.name, size: file.size };
        });
    },

    uploadFiles: async function (input, batchId, kind, mediaType, dotnet) {
        const files = Array.from(input.files || []);
        const results = [];
        const totalBytes = files.reduce(function (sum, file) { return sum + file.size; }, 0);
        let completedBytes = 0;

        for (let index = 0; index < files.length; index++) {
            const file = files[index];
            const query = new URLSearchParams({
                batchId: batchId,
                kind: kind || 'auto',
                mediaType: mediaType || '',
                fileName: file.name
            });

            const result = await new Promise(function (resolve, reject) {
                const xhr = new XMLHttpRequest();
                xhr.open('POST', '/api/tv/uploads?' + query.toString());
                xhr.setRequestHeader('X-Stackarr-Upload', '1');
                xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream');

                xhr.upload.onprogress = function (event) {
                    if (!event.lengthComputable || !dotnet) {
                        return;
                    }

                    dotnet.invokeMethodAsync('OnUploadProgress', index, files.length, completedBytes + event.loaded, totalBytes)
                        .catch(function () { /* the circuit may have navigated away */ });
                };

                xhr.onload = function () {
                    if (xhr.status >= 200 && xhr.status < 300) {
                        try {
                            resolve(JSON.parse(xhr.responseText));
                        } catch (error) {
                            reject(new Error('The upload returned an unreadable response.'));
                        }
                        return;
                    }

                    let message = 'Upload failed (' + xhr.status + ').';
                    try {
                        const body = JSON.parse(xhr.responseText);
                        if (body && body.error) {
                            message = body.error;
                        }
                    } catch (error) { /* keep the status message */ }
                    reject(new Error(message));
                };

                xhr.onerror = function () { reject(new Error('The upload connection failed.')); };
                xhr.onabort = function () { reject(new Error('The upload was cancelled.')); };
                xhr.send(file);
            });

            results.push(result);
            completedBytes += file.size;
        }

        input.value = '';
        return results;
    }
};
