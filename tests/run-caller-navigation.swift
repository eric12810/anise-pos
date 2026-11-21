import Foundation
import JavaScriptCore

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let scriptPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "src/AnisePOS.Api/wwwroot/caller-notifications.js"
let sourceURL = scriptPath.hasPrefix("/") ? URL(fileURLWithPath: scriptPath) : root.appendingPathComponent(scriptPath)
let source = try String(contentsOf: sourceURL, encoding: .utf8)
let harness = try String(contentsOf: root.appendingPathComponent("tests/caller-navigation-harness.js"), encoding: .utf8)
let context = JSContext()!
context.exceptionHandler = { _, exception in
    fputs("Caller navigation test failed: \(exception?.toString() ?? "unknown error")\n", stderr)
    exit(1)
}
context.setObject(source, forKeyedSubscript: "callerSource" as NSString)
let result = context.evaluateScript(harness)
print("Caller navigation checks passed: \(result?.toInt32() ?? 0)")
