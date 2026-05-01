//
//  Item.swift
//  OmniPlay
//
//  Created by nan on 2026/3/16.
//

import Foundation
import SwiftData

@Model
final class Item {
    var timestamp: Date
    
    init(timestamp: Date) {
        self.timestamp = timestamp
    }
}
