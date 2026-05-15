var UrlParams = {
    GetUrlParameter: function(keyPtr) {
        var key = UTF8ToString(keyPtr);
        var params = new URLSearchParams(window.location.search);
        var val = params.get(key) || '';
        var buf = _malloc(lengthBytesUTF8(val) + 1);
        stringToUTF8(val, buf, lengthBytesUTF8(val) + 1);
        return buf;
    }
};
mergeInto(LibraryManager.library, UrlParams);
