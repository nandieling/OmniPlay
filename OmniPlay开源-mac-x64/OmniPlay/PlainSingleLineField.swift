import AppKit
import SwiftUI

/// 直接挂在 NSTextField 上的单行输入框。
///
/// 分组表单（Form + .grouped）会把原生 SwiftUI 输入控件重新排进
/// 「标签左、控件右」的行布局，输入框因此被推离左缘；NSView 层不参与
/// 这套启发式布局，输入框才能像 TMDB API 输入框一样稳稳贴左。
struct PlainSingleLineField: NSViewRepresentable {
    @Binding var text: String
    var isSecure: Bool = false
    var onSubmit: (() -> Void)? = nil

    func makeNSView(context: Context) -> NSTextField {
        let field: NSTextField = isSecure ? NSSecureTextField() : NSTextField()
        field.delegate = context.coordinator
        field.stringValue = text
        field.font = .systemFont(ofSize: 13)
        field.isBezeled = true
        field.bezelStyle = .roundedBezel
        field.isEditable = true
        field.isSelectable = true
        field.lineBreakMode = .byClipping
        if let cell = field.cell {
            cell.wraps = false
            cell.isScrollable = true
            cell.usesSingleLineMode = true
            cell.lineBreakMode = .byClipping
        }
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return field
    }

    func updateNSView(_ nsView: NSTextField, context: Context) {
        if nsView.stringValue != text {
            nsView.stringValue = text
        }
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(self)
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        private var parent: PlainSingleLineField

        init(_ parent: PlainSingleLineField) {
            self.parent = parent
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            parent.text = field.stringValue
        }

        // 回车：提交当前内容并触发回调（等价于 SwiftUI 的 onSubmit）
        func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            guard commandSelector == #selector(NSResponder.insertNewline(_:)) else { return false }
            parent.text = control.stringValue
            control.window?.makeFirstResponder(control.window?.contentView)
            parent.onSubmit?()
            return true
        }
    }
}
