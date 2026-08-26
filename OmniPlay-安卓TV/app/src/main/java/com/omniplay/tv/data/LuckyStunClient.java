package com.omniplay.tv.data;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Set;

public final class LuckyStunClient {
    private static final String[] LOGIN_PATHS = {
            "/api/login", "/api/auth/login", "/api/user/login", "/api/auth/signin", "/login"
    };
    private static final String[] RULE_PATHS = {
            "/api/stun", "/api/stun/rules", "/api/stun/list", "/api/penetration",
            "/api/penetration/rules", "/api/nat/stun", "/api/config/stun", "/api/rules", "/api/getstunlist"
    };

    private LuckyStunClient() {
    }

    public static Result fetch(String managementUrl, String username, String password, String selectedRuleId, String selectedRuleName)
            throws IOException, JSONException {
        String baseUrl = normalizeManagementUrl(managementUrl);
        if (baseUrl.isEmpty()) {
            throw new IOException("Lucky STUN 管理地址无效。");
        }

        ArrayList<String> cookies = new ArrayList<>();
        String token = "";
        if (!trim(username).isEmpty() || password != null && !password.isEmpty()) {
            LoginResult login = login(baseUrl, trim(username), password == null ? "" : password, cookies);
            token = login.token;
            cookies = login.cookies;
        }

        String lastError = "没有识别到规则列表。";
        for (String path : RULE_PATHS) {
            try {
                Response response = request("GET", baseUrl + path, null, token, cookies);
                if (response.status < 200 || response.status >= 300) {
                    lastError = "HTTP " + response.status;
                    continue;
                }
                List<Rule> rules = parseRules(response.body);
                if (!rules.isEmpty()) {
                    Rule selected = selectRule(rules, selectedRuleId, selectedRuleName);
                    return new Result(rules, selected, "已读取 " + rules.size() + " 条规则。");
                }
                lastError = "响应中没有识别到穿透规则。";
            } catch (JSONException error) {
                lastError = "规则响应解析失败：" + error.getMessage();
            }
        }
        throw new IOException("无法读取 Lucky STUN 规则：" + lastError);
    }

    public static String normalizeAddress(String value) {
        String trimmed = trim(value);
        if (trimmed.isEmpty()) {
            return "";
        }
        if (!trimmed.startsWith("http://") && !trimmed.startsWith("https://")) {
            trimmed = "http://" + trimmed;
        }
        try {
            URL url = new URL(trimmed);
            if (url.getHost() == null || url.getHost().isEmpty()) {
                return "";
            }
        } catch (Exception ignored) {
            return "";
        }
        while (trimmed.endsWith("/")) {
            trimmed = trimmed.substring(0, trimmed.length() - 1);
        }
        return trimmed;
    }

    private static LoginResult login(String baseUrl, String username, String password, ArrayList<String> cookies) throws IOException, JSONException {
        JSONObject body = new JSONObject();
        body.put("username", username);
        body.put("password", password);
        int lastStatus = 400;
        for (String path : LOGIN_PATHS) {
            try {
                Response response = request("POST", baseUrl + path, body.toString(), "", cookies);
                cookies = mergeCookies(cookies, response.cookies);
                lastStatus = response.status;
                if (response.status < 200 || response.status >= 300) {
                    continue;
                }
                String token = findStringRecursive(response.body, new String[]{"token", "accessToken", "access_token", "jwt", "authorization"});
                if (token != null && !token.toLowerCase(Locale.ROOT).startsWith("bearer ")) {
                    token = "Bearer " + token;
                }
                return new LoginResult(token == null ? "" : token, cookies);
            } catch (IOException ignored) {
            }
        }
        if (lastStatus == 401 || lastStatus == 403) {
            throw new IOException("Lucky STUN 登录失败，请检查账号和密码。");
        }
        return new LoginResult("", cookies);
    }

    private static Response request(String method, String url, String body, String token, List<String> cookies) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
        connection.setConnectTimeout(6000);
        connection.setReadTimeout(12000);
        connection.setRequestMethod(method);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("User-Agent", "OmniPlay-Android-LuckySTUN");
        if (token != null && !token.isEmpty()) {
            connection.setRequestProperty("Authorization", token);
        }
        if (cookies != null && !cookies.isEmpty()) {
            connection.setRequestProperty("Cookie", join(cookies, "; "));
        }
        if (body != null) {
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            connection.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream output = connection.getOutputStream()) {
                output.write(bytes);
            }
        }
        int status = connection.getResponseCode();
        InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
        String text = readText(stream);
        ArrayList<String> responseCookies = new ArrayList<>();
        for (var entry : connection.getHeaderFields().entrySet()) {
            if (entry.getKey() != null && "Set-Cookie".equalsIgnoreCase(entry.getKey()) && entry.getValue() != null) {
                for (String header : entry.getValue()) {
                    if (header != null && !header.isEmpty()) {
                        responseCookies.add(header.split(";", 2)[0]);
                    }
                }
            }
        }
        connection.disconnect();
        return new Response(status, text, responseCookies);
    }

    private static List<Rule> parseRules(String body) throws JSONException {
        Object root = body.trim().startsWith("[") ? new JSONArray(body) : new JSONObject(body);
        ArrayList<Rule> rules = new ArrayList<>();
        collectRules(root, "Lucky STUN 规则", rules);
        Set<String> seen = new HashSet<>();
        ArrayList<Rule> unique = new ArrayList<>();
        for (Rule rule : rules) {
            String key = (rule.id + "|" + rule.name + "|" + rule.address).toLowerCase(Locale.ROOT);
            if (seen.add(key)) {
                unique.add(rule);
            }
        }
        return unique;
    }

    private static void collectRules(Object value, String fallbackName, List<Rule> rules) throws JSONException {
        if (value instanceof JSONArray) {
            JSONArray array = (JSONArray) value;
            for (int i = 0; i < array.length(); i++) {
                collectRules(array.get(i), fallbackName, rules);
            }
            return;
        }
        if (!(value instanceof JSONObject)) {
            return;
        }
        JSONObject object = (JSONObject) value;
        String id = findString(object, new String[]{"id", "ruleId", "uuid", "key", "index"});
        String name = findString(object, new String[]{"name", "title", "ruleName", "remark", "description"});
        if (name == null || name.isEmpty()) {
            name = fallbackName;
        }
        String address = findAddress(object);
        if (address != null && (address.contains(".") || address.contains(":"))) {
            String normalized = normalizeAddress(address);
            if (!normalized.isEmpty()) {
                rules.add(new Rule(id == null || id.isEmpty() ? name + ":" + normalized : id, name, normalized));
            }
        }
        Iterator<String> keys = object.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            Object child = object.opt(key);
            if (child instanceof JSONObject || child instanceof JSONArray) {
                collectRules(child, name, rules);
            }
        }
    }

    private static String findAddress(JSONObject object) {
        String value = findString(object, new String[]{"address", "url", "remoteAddress", "publicAddress", "externalAddress", "penetrationAddress", "forwardAddress", "stunAddress", "remoteUrl", "domain", "host"});
        if (value != null && !value.toLowerCase(Locale.ROOT).contains("/api/")) {
            return value;
        }
        String host = findString(object, new String[]{"remoteHost", "publicHost", "externalHost"});
        String port = findString(object, new String[]{"remotePort", "publicPort", "externalPort", "port"});
        return host == null || host.isEmpty() || port == null || port.isEmpty() ? null : host + ":" + port;
    }

    private static Rule selectRule(List<Rule> rules, String id, String name) {
        String selectedId = trim(id);
        String selectedName = trim(name);
        for (Rule rule : rules) {
            if (!selectedId.isEmpty() && rule.id.equalsIgnoreCase(selectedId)) {
                return rule;
            }
        }
        for (Rule rule : rules) {
            if (!selectedName.isEmpty() && rule.name.equalsIgnoreCase(selectedName)) {
                return rule;
            }
        }
        return rules.isEmpty() ? null : rules.get(0);
    }

    private static String findString(JSONObject object, String[] keys) {
        Iterator<String> iterator = object.keys();
        while (iterator.hasNext()) {
            String name = iterator.next();
            for (String key : keys) {
                if (!name.equalsIgnoreCase(key)) {
                    continue;
                }
                Object value = object.opt(name);
                if (value instanceof String || value instanceof Number || value instanceof Boolean) {
                    return String.valueOf(value);
                }
            }
        }
        return null;
    }

    private static String findStringRecursive(String body, String[] keys) {
        try {
            Object root = body.trim().startsWith("[") ? new JSONArray(body) : new JSONObject(body);
            return findStringRecursive(root, keys);
        } catch (JSONException ignored) {
            return null;
        }
    }

    private static String findStringRecursive(Object value, String[] keys) throws JSONException {
        if (value instanceof JSONObject) {
            JSONObject object = (JSONObject) value;
            String direct = findString(object, keys);
            if (direct != null && !direct.isEmpty()) {
                return direct;
            }
            Iterator<String> iterator = object.keys();
            while (iterator.hasNext()) {
                String nested = findStringRecursive(object.opt(iterator.next()), keys);
                if (nested != null && !nested.isEmpty()) {
                    return nested;
                }
            }
        } else if (value instanceof JSONArray) {
            JSONArray array = (JSONArray) value;
            for (int i = 0; i < array.length(); i++) {
                String nested = findStringRecursive(array.get(i), keys);
                if (nested != null && !nested.isEmpty()) {
                    return nested;
                }
            }
        }
        return null;
    }

    private static ArrayList<String> mergeCookies(List<String> old, List<String> fresh) {
        ArrayList<String> result = new ArrayList<>(old == null ? java.util.Collections.emptyList() : old);
        for (String cookie : fresh) {
            String key = cookie.split("=", 2)[0];
            result.removeIf(item -> item.startsWith(key + "="));
            result.add(cookie);
        }
        return result;
    }

    private static String normalizeManagementUrl(String value) {
        String trimmed = trim(value);
        if (trimmed.isEmpty()) {
            return "";
        }
        if (!trimmed.contains("://")) {
            trimmed = "http://" + trimmed;
        }
        try {
            URL url = new URL(trimmed);
            if (!(url.getProtocol().equalsIgnoreCase("http") || url.getProtocol().equalsIgnoreCase("https")) || url.getHost().isEmpty()) {
                return "";
            }
        } catch (Exception ignored) {
            return "";
        }
        while (trimmed.endsWith("/")) {
            trimmed = trimmed.substring(0, trimmed.length() - 1);
        }
        if (trimmed.toLowerCase(Locale.ROOT).endsWith("/api")) {
            trimmed = trimmed.substring(0, trimmed.length() - 4);
            while (trimmed.endsWith("/")) {
                trimmed = trimmed.substring(0, trimmed.length() - 1);
            }
        }
        return trimmed;
    }

    private static String join(List<String> values, String separator) {
        StringBuilder builder = new StringBuilder();
        for (String value : values) {
            if (builder.length() > 0) {
                builder.append(separator);
            }
            builder.append(value);
        }
        return builder.toString();
    }

    private static String trim(String value) {
        return value == null ? "" : value.trim();
    }

    private static String readText(InputStream stream) throws IOException {
        if (stream == null) {
            return "";
        }
        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line);
            }
        }
        return builder.toString();
    }

    public static final class Rule {
        public final String id;
        public final String name;
        public final String address;

        Rule(String id, String name, String address) {
            this.id = id;
            this.name = name;
            this.address = address;
        }
    }

    public static final class Result {
        public final List<Rule> rules;
        public final Rule selectedRule;
        public final String message;

        Result(List<Rule> rules, Rule selectedRule, String message) {
            this.rules = rules;
            this.selectedRule = selectedRule;
            this.message = message;
        }
    }

    private static final class LoginResult {
        final String token;
        final ArrayList<String> cookies;

        LoginResult(String token, ArrayList<String> cookies) {
            this.token = token;
            this.cookies = cookies;
        }
    }

    private static final class Response {
        final int status;
        final String body;
        final ArrayList<String> cookies;

        Response(int status, String body, ArrayList<String> cookies) {
            this.status = status;
            this.body = body;
            this.cookies = cookies;
        }
    }
}
