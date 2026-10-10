// One bounded page-file mapping per worker. The caller holds its worker gate until
// WPF has copied the image, so a subsequent request cannot overwrite live pixels.
#pragma once
class SharedRaster {
    HANDLE handle_ = nullptr;
    uint8_t* data_ = nullptr;
    size_t capacity_ = 0;
    unsigned serial_ = 0;
    std::string name_;
public:
    ~SharedRaster() { Reset(); }
    void Reset() {
        if (data_) UnmapViewOfFile(data_);
        if (handle_) CloseHandle(handle_);
        data_ = nullptr; handle_ = nullptr; capacity_ = 0; name_.clear();
    }
    uint8_t* Acquire(size_t bytes) {
        if (bytes > 256u * 1024 * 1024) return nullptr;
        if (capacity_ >= bytes && capacity_ <= std::max<size_t>(16u << 20, bytes * 4)) return data_;
        Reset();
        size_t rounded = (bytes + 65535) & ~(size_t)65535;
        name_ = "Local\\XTPdfRaster-" + std::to_string(GetCurrentProcessId()) + "-" +
            std::to_string(GetTickCount64()) + "-" + std::to_string(++serial_);
        handle_ = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, (DWORD)rounded, name_.c_str());
        if (!handle_ || GetLastError() == ERROR_ALREADY_EXISTS) { Reset(); return nullptr; }
        data_ = (uint8_t*)MapViewOfFile(handle_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, rounded);
        if (!data_) { Reset(); return nullptr; }
        capacity_ = rounded;
        return data_;
    }
    size_t Capacity() const { return capacity_; }
    const std::string& Name() const { return name_; }
};

// A request-scoped event can interrupt MuPDF even while stdin is busy rendering.
// Windows' wait pool only invokes the callback on cancellation; no polling thread.
class RenderCancellation {
    HANDLE event_ = nullptr, wait_ = nullptr;
    static void CALLBACK Abort(void* value, BOOLEAN) {
        auto* self = (RenderCancellation*)value;
        InterlockedExchange((volatile LONG*)&self->cookie.abort, 1);
    }
public:
    fz_cookie cookie{};
    explicit RenderCancellation(const std::string& name) {
        if (name.rfind("Local\\XTPdfCancel-", 0) != 0) return;
        event_ = OpenEventA(SYNCHRONIZE, FALSE, name.c_str());
        if (!event_) return;
        if (WaitForSingleObject(event_, 0) == WAIT_OBJECT_0) cookie.abort = 1;
        if (!RegisterWaitForSingleObject(&wait_, event_, Abort, this, INFINITE, WT_EXECUTEONLYONCE)) wait_ = nullptr;
    }
    ~RenderCancellation() {
        // Wait for callbacks before cookie storage can be destroyed.
        if (wait_) UnregisterWaitEx(wait_, INVALID_HANDLE_VALUE);
        if (event_) CloseHandle(event_);
    }
    void Check() const { if (cookie.abort) throw std::runtime_error("render cancelled"); }
};
