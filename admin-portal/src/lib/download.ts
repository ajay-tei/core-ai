/**
 * Triggers a JSON file download in the browser.
 * @param data  Any serializable value.
 * @param filename  Suggested file name (should end in .json).
 */
export function triggerJsonDownload(data: unknown, filename: string): void
{
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}

/**
 * Reads a File object as text and returns the parsed JSON value.
 * Throws if the file cannot be parsed.
 */
export function readJsonFile<T = unknown>(file: File): Promise<T>
{
    return new Promise((resolve, reject) =>
    {
        const reader = new FileReader();
        reader.onload = (e) =>
        {
            try { resolve(JSON.parse(e.target?.result as string) as T); }
            catch { reject(new Error("File is not valid JSON.")); }
        };
        reader.onerror = () => reject(new Error("Failed to read file."));
        reader.readAsText(file);
    });
}
