import AppIntents
import Foundation

private typealias ShoppingHandler = @convention(c) (UnsafePointer<CChar>, UnsafePointer<CChar>, UnsafePointer<CChar>) -> Void

@MainActor private enum ShoppingBridge {
    static var handler: ShoppingHandler?
    static var pending: [String: CheckedContinuation<String, Error>] = [:]

    static func add(_ product: String, quantity: String?) async throws -> String {
        guard let handler else { throw ShoppingError.unavailable }
        let request = UUID().uuidString
        return try await withCheckedThrowingContinuation { continuation in
            pending[request] = continuation
            request.withCString { id in
                product.withCString { name in
                    (quantity ?? "").withCString { amount in handler(id, name, amount) }
                }
            }
        }
    }
}
private enum ShoppingError: LocalizedError {
    case unavailable
    case saveFailed(String)
    var errorDescription: String? {
        switch self {
        case .unavailable: return "Open Family App once and try again. Shopping is not ready yet."
        case .saveFailed(let message): return message
        }
    }
}

@_cdecl("FamilyShoppingRegister")
public func familyShoppingRegister(_ callback: @escaping @convention(c) (UnsafePointer<CChar>, UnsafePointer<CChar>, UnsafePointer<CChar>) -> Void) {
    Task { @MainActor in
        ShoppingBridge.handler = callback
        if #available(iOS 16, *) { FamilyShoppingShortcuts.updateAppShortcutParameters() }
    }
}

@_cdecl("FamilyShoppingComplete")
public func familyShoppingComplete(_ request: UnsafePointer<CChar>, _ succeeded: Int32, _ message: UnsafePointer<CChar>) {
    let id = String(cString: request)
    let text = String(cString: message)
    Task { @MainActor in
        guard let continuation = ShoppingBridge.pending.removeValue(forKey: id) else { return }
        if succeeded != 0 { continuation.resume(returning: text) }
        else { continuation.resume(throwing: ShoppingError.saveFailed(text)) }
    }
}

@available(iOS 16, *)
struct AddFamilyShopping: AppIntent {
    static var title: LocalizedStringResource = "Add to family shopping"
    static var description = IntentDescription("Add a product to your family's local shopping list.")
    static var openAppWhenRun: Bool = true
    static var authenticationPolicy: IntentAuthenticationPolicy = .requiresLocalDeviceAuthentication
    @Parameter(title: "Product", requestValueDialog: "What would you like to add?") var product: String
    @Parameter(title: "Quantity") var quantity: String?
    static var parameterSummary: some ParameterSummary { Summary("Add \(\.$product) to family shopping") }
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog {
        let result = try await ShoppingBridge.add(product, quantity: quantity)
        return .result(dialog: IntentDialog(stringLiteral: result))
    }
}
@available(iOS 16, *)
struct FamilyShoppingShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: AddFamilyShopping(), phrases: [
            "Add groceries with \(.applicationName)",
            "Add to shopping with \(.applicationName)"
        ], shortTitle: "Add to shopping", systemImageName: "cart.badge.plus")
    }
}
