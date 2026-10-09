package com.omniplay.tv.local;

/**
 * One row in the folder picker. A plain {@code java.io.File} cannot carry this: it has no
 * way to say "directory" without putting the flag in the path, and {@code new File(name, "d")}
 * silently turns {@code getName()} into "d" for every row.
 */
public final class BrowseEntry implements Comparable<BrowseEntry> {
    public final String name;
    public final String url;
    public final boolean directory;

    public BrowseEntry(String name, String url, boolean directory) {
        this.name = name == null ? "" : name;
        this.url = url == null ? "" : url;
        this.directory = directory;
    }

    @Override
    public int compareTo(BrowseEntry other) {
        if (directory != other.directory) {
            return directory ? -1 : 1;
        }
        return name.compareToIgnoreCase(other.name);
    }
}
